using System.Security.Cryptography;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;

namespace UEBulkExport;

public sealed record CompareSource(string Path, EGame? Game = null, IReadOnlyList<string>? AesKeys = null);

public sealed record CompareOptions(CompareSource Before, CompareSource After, bool HashContents = false,
    string? OodlePath = null, string? ZlibPath = null);

public enum CompareChange { Added, Removed, Changed }

public sealed record CompareEntry(CompareChange Change, string Path, long? BeforeSize, long? AfterSize)
{
    public string? BeforeContainer { get; init; }
    public string? AfterContainer { get; init; }
}

public sealed record CompareResult(bool HashContents, int Added, int Removed, int Changed, int Unchanged,
    IReadOnlyList<CompareEntry> Entries, string AfterDirectory, EGame AfterGame, bool CanExportAfter)
{
    public IReadOnlyList<CompareGroup> Groups { get; init; } = [];
    internal CompareSnapshot? BeforeSnapshot { get; init; }
    internal CompareSnapshot? AfterSnapshot { get; init; }
    internal IReadOnlyDictionary<string, string?> AfterContainers { get; init; } =
        new Dictionary<string, string?>();
}

/// <summary>Compares the paths exposed by two independently mounted Unreal builds.</summary>
public static class CompareBuilds
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public static CompareResult Run(CompareOptions options, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var before = Resolve(options.Before, "Before", ct);
        var after = Resolve(options.After, "After", ct);

        var initializedNatives = before.Containers.Any(IsIoStore) || after.Containers.Any(IsIoStore);
        if (initializedNatives) InitializeNatives();

        using var beforeProvider = Mount(before, "Before", ct);
        progress?.Report(0.1);
        using var afterProvider = Mount(after, "After", ct);
        progress?.Report(0.2);

        var beforeReaders = IoReaders(beforeProvider);
        var afterReaders = IoReaders(afterProvider);
        var beforeSnapshot = CompareSnapshot.Capture(before.Directory, before.Game, before.IsSingleContainer,
            PhysicalFiles(before, beforeReaders));
        var afterSnapshot = CompareSnapshot.Capture(after.Directory, after.Game, after.IsSingleContainer,
            PhysicalFiles(after, afterReaders));
        var beforeFiles = Index(beforeProvider, ct, out _);
        var afterFiles = Index(afterProvider, ct, out var hasDuplicatePaths);
        if (options.HashContents && !initializedNatives && beforeFiles.Any(pair =>
            {
                ct.ThrowIfCancellationRequested();
                return afterFiles.TryGetValue(pair.Key, out var later) && pair.Value.Size == later.Size &&
                       (UsesNativeCompression(pair.Value) || UsesNativeCompression(later));
            }))
            InitializeNatives();

        var canExport = !after.IsSingleContainer && !hasDuplicatePaths;
        return CompareEntries(beforeFiles, afterFiles, options.HashContents, after.Directory, after.Game,
            canExport, progress, ct, before.Directory, beforeReaders, afterReaders) with
        {
            BeforeSnapshot = beforeSnapshot,
            AfterSnapshot = afterSnapshot
        };

        void InitializeNatives() => Natives.Initialize(new Options
        {
            Mode = ExportMode.Raw,
            OodlePath = options.OodlePath,
            ZlibPath = options.ZlibPath
        });
    }

    private static IoStoreReader[] IoReaders(DefaultFileProvider provider) => provider.MountedVfs
        .Concat(provider.UnloadedVfs).OfType<IoStoreReader>().ToArray();

    private static IEnumerable<string> PhysicalFiles(Resolved source, IReadOnlyList<IoStoreReader> readers) =>
        source.Containers.Concat(readers.SelectMany(reader => reader.ContainerStreams.Select(stream => stream.Name)));

    private static bool UsesNativeCompression(GameFile file) =>
        file.CompressionMethod is CompressionMethod.Zlib or CompressionMethod.Oodle;

    internal static CompareResult CompareEntries(IReadOnlyDictionary<string, GameFile> before,
        IReadOnlyDictionary<string, GameFile> after, bool hashContents, string afterDirectory, EGame afterGame,
        bool canExport, IProgress<double>? progress = null, CancellationToken ct = default,
        string? beforeDirectory = null, IReadOnlyList<IoStoreReader>? beforeReaders = null,
        IReadOnlyList<IoStoreReader>? afterReaders = null)
    {
        var entries = new List<CompareEntry>();
        var removed = 0;
        var added = 0;
        var changed = 0;
        var unchanged = 0;
        long examined = 0;
        var total = Math.Max(1, (long)before.Count + after.Count);
        var reportInterval = Math.Max(1, (total + 99) / 100);

        foreach (var (path, earlier) in before)
        {
            ct.ThrowIfCancellationRequested();
            if (!after.TryGetValue(path, out var later))
            {
                removed++;
                entries.Add(new(CompareChange.Removed, path, earlier.Size, null)
                    { BeforeContainer = ContainerPath(earlier, beforeDirectory) });
            }
            else if (earlier.Size != later.Size || (hashContents && !ContentEquals(earlier, later, ct)))
            {
                changed++;
                entries.Add(new(CompareChange.Changed, later.Path, earlier.Size, later.Size)
                {
                    BeforeContainer = ContainerPath(earlier, beforeDirectory),
                    AfterContainer = ContainerPath(later, afterDirectory)
                });
            }
            else unchanged++;
            ReportProgress();
        }

        foreach (var (path, later) in after)
        {
            ct.ThrowIfCancellationRequested();
            if (!before.ContainsKey(path))
            {
                added++;
                entries.Add(new(CompareChange.Added, path, null, later.Size)
                    { AfterContainer = ContainerPath(later, afterDirectory) });
            }
            ReportProgress();
        }

        entries.Sort((left, right) => Paths.Compare(left.Path, right.Path));
        var groups = CompareGroups.Build(entries, before, after, beforeReaders ?? [], afterReaders ?? [], ct);
        var afterContainers = new Dictionary<string, string?>(Paths);
        foreach (var path in groups.SelectMany(group => group.AfterPaths))
        {
            ct.ThrowIfCancellationRequested();
            afterContainers.TryAdd(path, ContainerPath(after[path], afterDirectory));
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(1);
        return new(hashContents, added, removed, changed, unchanged, entries.AsReadOnly(), afterDirectory,
            afterGame, canExport) { Groups = groups, AfterContainers = afterContainers };

        void ReportProgress()
        {
            if (++examined % reportInterval == 0) progress?.Report(0.2 + 0.75 * examined / total);
        }
    }

    internal static string? ContainerPath(GameFile file, string? directory) =>
        directory is not null && file is VfsEntry entry
            ? Path.GetRelativePath(directory, entry.Vfs.Path).Replace('\\', '/') : null;

    private static bool ContentEquals(GameFile before, GameFile after, CancellationToken ct)
    {
        var beforeHash = ReadHash(before, "Before", ct);
        ct.ThrowIfCancellationRequested();
        return beforeHash.AsSpan().SequenceEqual(ReadHash(after, "After", ct));
    }

    private static byte[] ReadHash(GameFile file, string side, CancellationToken ct)
    {
        const int blockBytes = 4 * 1024 * 1024;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // Stock readers accept an offset and length via Extract. Game-specific reader overrides
            // can transform or decode whole entries and may ignore a requested partial read.
            if (file.Size > blockBytes && file is VfsEntry entry && (file is FPakEntry or FIoStoreEntry) &&
                Retoc.ToRetocEngineVersion(entry.Vfs.Game.ToString()) is not null)
            {
                for (long offset = 0; offset < file.Size; offset += blockBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(blockBytes, file.Size - offset);
                    var bytes = file.Read(new FByteBulkDataHeader(default, count, (uint)count, offset, default));
                    if (bytes.Length != count) throw new IOException("Reader returned an incomplete range.");
                    hash.AppendData(bytes);
                }
            }
            else
            {
                var bytes = file.Read();
                for (var offset = 0; offset < bytes.Length;)
                {
                    ct.ThrowIfCancellationRequested();
                    var count = Math.Min(blockBytes, bytes.Length - offset);
                    hash.AppendData(bytes.AsSpan(offset, count));
                    offset += count;
                }
            }
            ct.ThrowIfCancellationRequested();
            return hash.GetHashAndReset();
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new UserFacingException($"{side} build: could not read '{file.Path}' for hashing.",
                "Check the container, its AES key and compression libraries before comparing its contents.");
        }
    }

    private sealed record Resolved(string Directory, EGame Game, string[] Containers, bool IsSingleContainer,
        IReadOnlyList<string> AesKeys);

    private static Resolved Resolve(CompareSource source, string side, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.Path))
            throw new UserFacingException($"{side} build: choose a folder or container.");

        try
        {
            var path = Path.GetFullPath(source.Path.Trim().Trim('"'));
            var directory = Discovery.ResolvePaksDirectory(path);
            var single = File.Exists(path);
            if (single && Path.GetExtension(path).Equals(".ucas", StringComparison.OrdinalIgnoreCase))
            {
                var expected = Path.ChangeExtension(path, ".utoc");
                path = Directory.EnumerateFiles(directory).FirstOrDefault(file => Paths.Equals(file, expected))
                    ?? throw new UserFacingException("The selected .ucas needs its matching .utoc.",
                        "Choose the table of contents (.utoc) file for this container.");
            }

            string[] containers = single ? [path] : Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                })
                .Where(file =>
                {
                    ct.ThrowIfCancellationRequested();
                    return IsIoStore(file) || Path.GetExtension(file).Equals(".pak", StringComparison.OrdinalIgnoreCase);
                })
                .Order(Paths).ThenBy(file => file, StringComparer.Ordinal).ToArray();

            if (containers.Length == 0)
                throw new UserFacingException("No .pak or .utoc files were found.");

            foreach (var key in source.AesKeys ?? []) Cli.ValidateAesKey(key);
            var game = source.Game ?? DetectGame(directory, ct);
            return new(directory, game, containers, single, source.AesKeys ?? []);
        }
        catch (UserFacingException e)
        {
            throw new UserFacingException($"{side} build: {e.Headline}", e.Hint);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new UserFacingException($"{side} build: the source could not be opened.", e.Message);
        }
    }

    private static bool IsIoStore(string path) =>
        Path.GetExtension(path).Equals(".utoc", StringComparison.OrdinalIgnoreCase);

    private static EGame DetectGame(string directory, CancellationToken ct)
    {
        if (EngineDetection.DetectFromContainers(directory, ct) is { } detected)
        {
            try { return Cli.ParseGame(detected); }
            catch (UserFacingException) { }
        }
        return EGame.GAME_UE5_3;
    }

    private static DefaultFileProvider Mount(Resolved source, string side, CancellationToken ct)
    {
        var provider = new DefaultFileProvider(source.Directory, SearchOption.AllDirectories,
            new VersionContainer(source.Game), Paths);
        try
        {
            foreach (var container in source.Containers)
            {
                ct.ThrowIfCancellationRequested();
                var count = provider.UnloadedVfs.Count;
                provider.RegisterVfs(container);
                if (provider.UnloadedVfs.Count == count)
                    throw new UserFacingException($"{side} build: could not open '{Path.GetFileName(container)}'.",
                        "Check the container and its companion files, or choose the matching engine version.");
            }

            ContainerAesKeys.Submit(provider, source.AesKeys, ct);
            provider.Mount();
            ct.ThrowIfCancellationRequested();

            var unavailable = provider.UnloadedVfs.FirstOrDefault(reader =>
                reader.HasDirectoryIndex || !reader.Name.Equals("global.utoc", StringComparison.OrdinalIgnoreCase));
            if (unavailable is not null)
                throw new UserFacingException($"{side} build: '{unavailable.Name}' could not be mounted.",
                    "Supply the correct AES key for that build and verify its container and engine version.");
            if (provider.MountedVfs.Count == 0)
                throw new UserFacingException($"{side} build: no asset containers were mounted.");
            return provider;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    private static Dictionary<string, GameFile> Index(DefaultFileProvider provider, CancellationToken ct,
        out bool hasDuplicatePaths)
    {
        var files = new Dictionary<string, GameFile>(Paths);
        hasDuplicatePaths = false;
        foreach (var archive in provider.MountedVfs.OrderByDescending(reader => reader.ReadOrder)
                     .ThenBy(reader => reader.Path, Paths).ThenBy(reader => reader.Path, StringComparer.Ordinal))
        {
            foreach (var file in archive.Files.Values)
            {
                ct.ThrowIfCancellationRequested();
                if (!files.TryAdd(file.Path, file)) hasDuplicatePaths = true;
            }
        }
        return files;
    }
}
