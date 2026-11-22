using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UEBulkExport;

internal static class DumpConversionFreshness
{
    private const string RevisionMarker = ".uebulk-source-revision";
    private const int RevisionFolderLength = 16;
    private static readonly byte[] NameSeparator = [0];

    public static async Task<string> OpenCookedStageAsync(string exportRoot, string source,
        IReadOnlyList<string> cookedInputs, DumpConversionOptions options, CancellationToken ct)
    {
        using var revision = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddName(revision, "cooked-stage-v1");
        AddName(revision, source.ToUpperInvariant());
        AddName(revision, options.SourceVersion);
        foreach (var path in cookedInputs.Order(StringComparer.OrdinalIgnoreCase)
                     .ThenBy(path => path, StringComparer.Ordinal))
            await AppendFileAsync(revision, path, Path.GetRelativePath(source, path), ct);

        // Helpers and mappings can change the intermediates even when the packages do not.
        foreach (var (name, path) in new[]
                 {
                     ("UE Viewer", options.UModelPath),
                     ("Mappings", options.MappingsPath),
                     ("vgmstream", options.VgmStreamPath)
                 })
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                await AppendFileAsync(revision, path, name, ct);
        }

        var fingerprint = Convert.ToHexString(revision.GetHashAndReset());
        var revisions = Path.Combine(exportRoot, ".uebulk-revisions");
        // The full digest lives in the marker to leave room for long UE Viewer output paths.
        var stage = Path.Combine(revisions, fingerprint[..RevisionFolderLength]);
        ct.ThrowIfCancellationRequested();
        EnsureLocalDirectory(exportRoot);
        EnsureLocalDirectory(revisions);
        EnsureLocalDirectory(stage);
        var marker = Path.Combine(stage, RevisionMarker);
        if (File.Exists(marker))
        {
            if (!string.Equals(ReadRevisionMarker(stage), fingerprint, StringComparison.Ordinal))
                throw new UserFacingException("The staging folder has an invalid revision marker.", stage);
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(stage).Any())
                throw new UserFacingException("The staging folder belongs to another operation.", stage);
            AtomicFile.WriteAllBytes(marker, Encoding.UTF8.GetBytes(fingerprint));
        }

        return stage;
    }

    public static void PruneCookedStages(string current)
    {
        var revisions = Path.GetDirectoryName(current)!;
        try
        {
            if (File.GetAttributes(revisions).HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var directory in Directory.EnumerateDirectories(revisions))
            {
                if (directory.Equals(current, StringComparison.OrdinalIgnoreCase) ||
                    ReadRevisionMarker(directory) is null) continue;
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Could not remove old migration intermediates '{directory}': {e.Message}");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not inspect old migration intermediates in '{revisions}': {e.Message}");
        }
    }

    public static async Task<string> FingerprintImportAsync(string path, string importRoot, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await AppendFileAsync(hash, path, Path.GetRelativePath(importRoot, path), ct);
        if (Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            var dependencies = await FindGltfInputsAsync(path, importRoot, ct);
            foreach (var dependency in dependencies.Order(StringComparer.OrdinalIgnoreCase))
                await AppendFileAsync(hash, dependency, Path.GetRelativePath(importRoot, dependency), ct);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<IReadOnlySet<string>> FindGltfInputsAsync(string path, string importRoot,
        CancellationToken ct)
    {
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var input = OpenRead(path);
            using var document = await JsonDocument.ParseAsync(input, cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return dependencies;
            foreach (var section in new[] { "buffers", "images" })
            {
                if (!document.RootElement.TryGetProperty(section, out var elements) ||
                    elements.ValueKind != JsonValueKind.Array) continue;
                foreach (var element in elements.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object ||
                        !element.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String) continue;
                    if (ResolveLocalInput(path, importRoot, uri.GetString()!) is { } dependency)
                        dependencies.Add(dependency);
                }
            }
        }
        catch (JsonException)
        {
            // Keep the primary file fingerprint. Unreal's importer reports invalid glTF content.
        }

        return dependencies;
    }

    private static string? ResolveLocalInput(string sourceFile, string importRoot, string uri)
    {
        var value = Uri.UnescapeDataString(uri);
        if (Uri.TryCreate(value, UriKind.Absolute, out _) || Path.IsPathRooted(value)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, value));
            var relative = Path.GetRelativePath(importRoot, path);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                !File.Exists(path)) return null;
            for (var current = path; !current.Equals(importRoot, StringComparison.OrdinalIgnoreCase);
                 current = Path.GetDirectoryName(current)!)
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return null;
            return path;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static async Task AppendFileAsync(IncrementalHash hash, string path, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AddName(hash, name.Replace('\\', '/'));
        await using var input = OpenRead(path);
        hash.AppendData(await SHA256.HashDataAsync(input, ct));
    }

    private static void AddName(IncrementalHash hash, string name)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(name));
        hash.AppendData(NameSeparator);
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static string? ReadRevisionMarker(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name.Length != RevisionFolderLength || !name.All(Uri.IsHexDigit) ||
            File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) return null;
        var marker = new FileInfo(Path.Combine(directory, RevisionMarker));
        if (!marker.Exists || marker.Length != 64 || marker.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return null;
        var fingerprint = File.ReadAllText(marker.FullName);
        return fingerprint.StartsWith(name, StringComparison.Ordinal) && fingerprint.All(Uri.IsHexDigit)
            ? fingerprint
            : null;
    }

    private static void EnsureLocalDirectory(string directory)
    {
        if (Directory.CreateDirectory(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UserFacingException("The migration staging folder must be a local directory.", directory);
    }
}
