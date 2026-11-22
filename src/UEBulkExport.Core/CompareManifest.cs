using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Versions;

namespace UEBulkExport;

public sealed record CompareManifestSource(EGame Game, bool SingleContainer, string Fingerprint);

public sealed record CompareManifestVerification(CompareResult Result, IReadOnlySet<string> SelectedPaths)
{
    internal void CheckExportFiles(IReadOnlyList<GameFile> files)
    {
        if (files.Count != SelectedPaths.Count || files.Any(file =>
                !Result.AfterContainers.TryGetValue(file.Path, out var expected) ||
                !StringComparer.OrdinalIgnoreCase.Equals(expected, CompareBuilds.ContainerPath(file, Result.AfterDirectory))))
            throw new UserFacingException("The export cannot resolve the full verified file set.",
                "Check that the after archives have not changed since verification.");
    }
}

/// <summary>A portable comparison and export scope, tied to the original archive bytes.</summary>
public sealed class CompareManifest
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    public const int CurrentVersion = 1;
    public required int Version { get; init; }
    public required bool HashContents { get; init; }
    public required CompareManifestSource Before { get; init; }
    public required CompareManifestSource After { get; init; }
    public required IReadOnlyList<CompareGroup> Groups { get; init; }

    public static CompareManifest Capture(CompareResult result, IReadOnlySet<string>? selectedGroupIds = null,
        CancellationToken ct = default)
    {
        if (result.BeforeSnapshot is not { } before || result.AfterSnapshot is not { } after)
            throw new UserFacingException("Compare the archives before saving a manifest.");

        var groups = selectedGroupIds is null ? result.Groups : result.Groups
            .Where(group => selectedGroupIds.Contains(group.Id)).ToArray();
        if (selectedGroupIds is not null && groups.Count != selectedGroupIds.Count)
            throw new UserFacingException("A selected group no longer belongs to this comparison.");

        var manifest = new CompareManifest
        {
            Version = CurrentVersion,
            HashContents = result.HashContents,
            Before = before.Identity(ct),
            After = after.Identity(ct),
            Groups = groups
        };
        manifest.Validate();
        return manifest;
    }

    public void Save(string path)
    {
        Validate();
        var destination = Path.GetFullPath(path);
        var existing = File.Exists(destination);
        if (existing) _ = Load(destination);
        var temporary = AtomicFile.CreateTemporaryPath(destination);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: existing);
        }
        finally
        {
            AtomicFile.TryDelete(temporary);
        }
    }

    public static CompareManifest Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var manifest = JsonSerializer.Deserialize<CompareManifest>(stream, Json) ?? throw InvalidManifest();
            manifest.Validate();
            return manifest;
        }
        catch (JsonException) { throw InvalidManifest(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new UserFacingException("The comparison manifest could not be opened.",
                "Choose a readable comparison manifest JSON file.");
        }
    }

    public CompareManifestVerification Verify(CompareSource before, CompareSource after,
        string? oodlePath = null, string? zlibPath = null, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Validate();
        var result = CompareBuilds.Run(new(before with { Game = Before.Game }, after with { Game = After.Game },
            HashContents, oodlePath, zlibPath), progress is null ? null : new CompareProgress(progress), ct);

        if (result.BeforeSnapshot!.Identity(ct) != Before)
            throw SourceMismatch("Before");
        progress?.Report(0.85);
        if (result.AfterSnapshot!.Identity(ct) != After)
            throw SourceMismatch("After");

        var byId = result.Groups.ToDictionary(group => group.Id, Paths);
        var selected = new HashSet<string>(Paths);
        foreach (var group in Groups)
        {
            ct.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(group.Id, out var verified) || group.Kind != verified.Kind ||
                group.Change != verified.Change || group.Path != verified.Path ||
                !group.Changes.SequenceEqual(verified.Changes) || !group.AfterPaths.SequenceEqual(verified.AfterPaths))
                throw new UserFacingException("The saved group differs from the verified comparison.",
                    "Save a new manifest from the comparison results.");
            selected.UnionWith(verified.AfterPaths);
        }

        progress?.Report(1);
        return new(result, selected);
    }

    private sealed class CompareProgress(IProgress<double> progress) : IProgress<double>
    {
        public void Report(double value) => progress.Report(0.7 * value);
    }

    private void Validate()
    {
        if (Version != CurrentVersion)
            throw new UserFacingException("This comparison manifest version is unsupported.");
        if (!ValidSource(Before) || !ValidSource(After) || Groups is null) throw InvalidManifest();

        var groupIds = new HashSet<string>(Paths);
        foreach (var group in Groups)
        {
            if (group is null || !Enum.IsDefined(group.Kind) || !Enum.IsDefined(group.Change) ||
                !ValidGroupId(group) || !groupIds.Add(group.Id) || !PortablePath(group.Path) ||
                group.AfterPaths is null || group.Changes is null || group.Changes.Count == 0 ||
                (group.AfterPaths.Count == 0) != (group.Change == CompareChange.Removed) ||
                group.AfterPaths.Any(path => !PortablePath(path)) ||
                group.Changes.Any(change => change is null || !Enum.IsDefined(change.Change) ||
                    !PortablePath(change.Path) ||
                    change.BeforeContainer is not null && !PortablePath(change.BeforeContainer) ||
                    change.AfterContainer is not null && !PortablePath(change.AfterContainer)))
                throw InvalidManifest();
        }
    }

    private static bool ValidSource(CompareManifestSource? source) => source is not null && Enum.IsDefined(source.Game) &&
        source.Fingerprint is { Length: 64 } && source.Fingerprint.All(Uri.IsHexDigit);

    private static bool ValidGroupId(CompareGroup group) => group.Kind switch
    {
        CompareGroupKind.File => group.Id is { } file && file.StartsWith("file:", StringComparison.Ordinal) &&
            PortablePath(file[5..]),
        CompareGroupKind.PakPackage => group.Id is { } pak && pak.StartsWith("pak:", StringComparison.Ordinal) &&
            PortablePath(pak[4..]),
        CompareGroupKind.IoStorePackage => group.Id is { Length: 19 } io && io.StartsWith("io:", StringComparison.Ordinal) &&
            io[3..].All(Uri.IsHexDigit),
        _ => false
    };

    private static bool PortablePath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        path.IndexOfAny(['\\', ':', '\0']) < 0 && path.Split('/').All(part => part is not ("" or "." or ".."));

    private static UserFacingException InvalidManifest() => new("The comparison manifest is invalid.",
        "Choose the original JSON file or save a new manifest from a comparison.");

    private static UserFacingException SourceMismatch(string side) => new($"{side} build differs from the saved manifest.",
        "Choose the original archives and their matching parts, or compare these builds again.");
}

internal sealed class CompareSnapshot(EGame game, bool singleContainer, CompareSnapshot.Archive[] archives)
{
    internal sealed record Archive(string Path, string RelativeName, long Length, DateTime Modified);

    public static CompareSnapshot Capture(string directory, EGame game, bool singleContainer,
        IEnumerable<string> physicalFiles)
    {
        var entries = physicalFiles.Distinct(StringComparer.Ordinal).Select(path =>
        {
            var info = new FileInfo(path);
            return new Archive(info.FullName, Path.GetRelativePath(directory, info.FullName).Replace('\\', '/'),
                info.Length, info.LastWriteTimeUtc);
        }).OrderBy(entry => entry.RelativeName, StringComparer.Ordinal).ToArray();

        return new(game, singleContainer, entries);
    }

    public CompareManifestSource Identity(CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("UEBulkExport.CompareManifest.Source.v1\0"u8);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            Span<byte> header = stackalloc byte[sizeof(int) + sizeof(long)];
            foreach (var archive in archives)
            {
                ct.ThrowIfCancellationRequested();
                EnsureUnchanged(archive);
                var name = Encoding.UTF8.GetBytes(archive.RelativeName);
                BinaryPrimitives.WriteInt32LittleEndian(header, name.Length);
                BinaryPrimitives.WriteInt64LittleEndian(header[sizeof(int)..], archive.Length);
                hash.AppendData(header);
                hash.AppendData(name);

                try
                {
                    using var stream = new FileStream(archive.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 1, FileOptions.SequentialScan);
                    int count;
                    long totalRead = 0;
                    while ((count = stream.Read(buffer)) != 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        totalRead += count;
                        hash.AppendData(buffer.AsSpan(0, count));
                    }
                    if (totalRead != archive.Length) throw ChangedDuringRead(archive);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new UserFacingException($"Could not fingerprint '{archive.RelativeName}'.",
                        "Check archive access before saving or verifying the comparison manifest.");
                }

                EnsureUnchanged(archive);
            }
            ct.ThrowIfCancellationRequested();
            return new(game, singleContainer, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureUnchanged(Archive archive)
    {
        var info = new FileInfo(archive.Path);
        if (!info.Exists || info.Length != archive.Length || info.LastWriteTimeUtc != archive.Modified)
            throw ChangedDuringRead(archive);
    }

    private static UserFacingException ChangedDuringRead(Archive archive) =>
        new($"'{archive.RelativeName}' changed after comparison started.", "Compare the builds again.");
}
