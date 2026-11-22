using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Versions;

namespace UEBulkExport;

public enum CompareGroupKind { File, PakPackage, IoStorePackage }

/// <summary>Changes and the complete set of addressable files for the group on the after side.</summary>
public sealed record CompareGroup(string Id, string Path, CompareGroupKind Kind, CompareChange Change,
    IReadOnlyList<CompareEntry> Changes, IReadOnlyList<string> AfterPaths);

internal static class CompareGroups
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> PakHeaders = new(["uasset", "umap"], Paths);
    private static readonly HashSet<string> PakParts = new(["uasset", "umap", "uexp", "ubulk", "uptnl"], Paths);

    private sealed record Package(string Id, string Path, CompareGroupKind Kind, string[] Members);
    private sealed record PackageIndex(Dictionary<string, Package> ByFile, Dictionary<string, Package> ById);

    public static IReadOnlyList<CompareGroup> Build(IReadOnlyList<CompareEntry> changes,
        IReadOnlyDictionary<string, GameFile> before, IReadOnlyDictionary<string, GameFile> after,
        IReadOnlyList<IoStoreReader> beforeReaders, IReadOnlyList<IoStoreReader> afterReaders, CancellationToken ct)
    {
        if (changes.Count == 0) return [];

        // Keep package membership only for changes. A one-file edit should not retain extra
        // groups for every unchanged package in the two builds.
        var pakStems = new HashSet<string>(Paths);
        var ioIds = new HashSet<ulong>();
        foreach (var entry in changes)
        {
            ct.ThrowIfCancellationRequested();
            AddCandidate(before.GetValueOrDefault(entry.Path));
            AddCandidate(after.GetValueOrDefault(entry.Path));
        }

        var earlier = Index(before.Values, beforeReaders, pakStems, ioIds, ct);
        var later = Index(after.Values, afterReaders, pakStems, ioIds, ct);
        var groups = new Dictionary<string, List<CompareEntry>>(Paths);
        foreach (var entry in changes)
        {
            ct.ThrowIfCancellationRequested();
            var package = entry.Change == CompareChange.Removed
                ? earlier.ByFile.GetValueOrDefault(entry.Path)
                : later.ByFile.GetValueOrDefault(entry.Path);
            var id = package?.Id ?? "file:" + entry.Path;
            GetMembers(groups, id).Add(entry);
        }

        return groups.Select(pair =>
        {
            ct.ThrowIfCancellationRequested();
            var (id, members) = pair;
            var beforePackage = earlier.ById.GetValueOrDefault(id);
            var afterPackage = later.ById.GetValueOrDefault(id);
            if ((afterPackage ?? beforePackage) is not { } package)
            {
                var entry = members[0];
                return new CompareGroup(id, entry.Path, CompareGroupKind.File, entry.Change, members.AsReadOnly(),
                    entry.Change == CompareChange.Removed ? [] : [entry.Path]);
            }

            var change = afterPackage is null ? CompareChange.Removed
                : beforePackage is null && members.All(entry => entry.Change == CompareChange.Added)
                    ? CompareChange.Added : CompareChange.Changed;
            return new CompareGroup(package.Id, package.Path, package.Kind, change, members.AsReadOnly(),
                afterPackage?.Members ?? []);
        }).OrderBy(group => group.Path, Paths).ThenBy(group => group.Id, Paths).ToArray();

        void AddCandidate(GameFile? file)
        {
            if (file is FIoStoreEntry io)
            {
                if (IsIoPackagePart(io.ChunkId, io.IoStoreReader.Game)) ioIds.Add(io.ChunkId.ChunkId);
            }
            else if (file is not null && IsPakPart(file))
                pakStems.Add(file.Path[..^(file.Extension.Length + 1)]);
        }
    }

    private static PackageIndex Index(IEnumerable<GameFile> files, IReadOnlyList<IoStoreReader> ioReaders,
        HashSet<string> pakStems, HashSet<ulong> ioIds, CancellationToken ct)
    {
        var byFile = new Dictionary<string, Package>(Paths);
        var byId = new Dictionary<string, Package>(Paths);
        var pakFiles = new Dictionary<string, List<GameFile>>(Paths);
        var ioFiles = new Dictionary<ulong, List<FIoStoreEntry>>();
        if (pakStems.Count == 0 && ioIds.Count == 0) return new(byFile, byId);
        var pakLookup = pakStems.GetAlternateLookup<ReadOnlySpan<char>>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file is FIoStoreEntry io)
            {
                if (ioIds.Contains(io.ChunkId.ChunkId) && IsIoPackagePart(io.ChunkId, io.IoStoreReader.Game))
                    GetMembers(ioFiles, io.ChunkId.ChunkId).Add(io);
            }
            else if (IsPakPart(file) &&
                     pakLookup.TryGetValue(file.Path.AsSpan(0, file.Path.Length - file.Extension.Length - 1), out var stem))
                GetMembers(pakFiles, stem).Add(file);
        }

        foreach (var parts in pakFiles.Values)
        {
            ct.ThrowIfCancellationRequested();
            var headers = parts.Where(file => PakHeaders.Contains(file.Extension)).Take(2).ToArray();
            if (headers.Length == 1)
                Add(new("pak:" + headers[0].Path[..^(headers[0].Extension.Length + 1)], headers[0].Path,
                    CompareGroupKind.PakPackage, OrderedPaths(parts)));
        }

        // The TOC also describes chunks missing from the directory index. An indexed chunk of the
        // same package ID is insufficient to establish that the raw exporter can address the whole package.
        var ioChunks = new Dictionary<ulong, List<(IoStoreReader Reader, FIoChunkId Chunk)>>();
        foreach (var reader in ioFiles.Count == 0 ? [] : ioReaders)
        foreach (var chunk in reader.TocResource.ChunkIds)
        {
            ct.ThrowIfCancellationRequested();
            if (ioFiles.ContainsKey(chunk.ChunkId) && IsIoPackagePart(chunk, reader.Game))
                GetMembers(ioChunks, chunk.ChunkId).Add((reader, chunk));
        }

        foreach (var (packageId, parts) in ioFiles)
        {
            ct.ThrowIfCancellationRequested();
            var headers = parts.Where(file => file.IsPackageData && !file.IsOptionalPackage).Take(2).ToArray();
            if (headers.Length != 1 || !ioChunks.TryGetValue(packageId, out var chunks) || chunks.Count != parts.Count)
                continue;

            var reader = headers[0].IoStoreReader;
            var indexed = new HashSet<FIoChunkId>();
            if (parts.Any(part => part.IoStoreReader != reader || !indexed.Add(part.ChunkId)) ||
                chunks.Any(chunk => chunk.Reader != reader || !indexed.Remove(chunk.Chunk)) || indexed.Count != 0)
                continue;

            Add(new($"io:{packageId:X16}", headers[0].Path, CompareGroupKind.IoStorePackage, OrderedPaths(parts)));
        }

        return new(byFile, byId);

        void Add(Package package)
        {
            byId.Add(package.Id, package);
            foreach (var path in package.Members) byFile.Add(path, package);
        }
    }

    private static bool IsIoPackagePart(FIoChunkId chunk, EGame game) => game >= EGame.GAME_UE5_0
        ? (EIoChunkType5)chunk.ChunkType is EIoChunkType5.ExportBundleData or EIoChunkType5.BulkData or
            EIoChunkType5.OptionalBulkData or EIoChunkType5.MemoryMappedBulkData
        : (EIoChunkType)chunk.ChunkType is EIoChunkType.ExportBundleData or EIoChunkType.BulkData or
            EIoChunkType.OptionalBulkData or EIoChunkType.MemoryMappedBulkData;

    private static string[] OrderedPaths(IEnumerable<GameFile> files) =>
        files.Select(file => file.Path).Order(Paths).ThenBy(path => path, StringComparer.Ordinal).ToArray();

    private static bool IsPakPart(GameFile file) => PakParts.Contains(file.Extension) &&
        file.Path.LastIndexOf('.') > file.Path.LastIndexOf('/');

    private static List<T> GetMembers<TKey, T>(Dictionary<TKey, List<T>> buckets, TKey key) where TKey : notnull
    {
        if (!buckets.TryGetValue(key, out var members)) buckets.Add(key, members = []);
        return members;
    }
}
