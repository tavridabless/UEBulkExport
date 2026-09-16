namespace UEBulkExport;

/// <summary>
/// Turns whatever path the user happened to have in the clipboard into the folder and files the
/// exporter actually needs. People point this tool at a game's root, at the executable's folder,
/// at a single .utoc, or at the Paks folder itself - all four should just work.
/// </summary>
public static class Discovery
{
    private static readonly string[] ContainerExtensions = [".utoc", ".pak", ".ucas"];

    /// <summary>How deep to look for a Paks folder when handed a game root.</summary>
    private const int MaxSearchDepth = 6;

    /// <summary>
    /// Resolves the folder holding the containers. Accepts the folder itself, any parent of it
    /// (a game root, for instance), or the path of a single .utoc/.pak/.ucas file.
    /// </summary>
    public static string ResolvePaksDirectory(string input)
    {
        var path = Path.GetFullPath(input.Trim().Trim('"'));

        if (File.Exists(path))
        {
            if (!ContainerExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new UserFacingException(
                    $"'{path}' is not an Unreal container.",
                    "Pass the folder holding the .utoc/.ucas/.pak files, or one of those files.");

            return Path.GetDirectoryName(path)!;
        }

        if (!Directory.Exists(path))
            throw new UserFacingException(
                $"Path not found: {path}",
                "Pass the folder holding the .utoc/.ucas/.pak files, the game's root folder, or a .utoc file.");

        if (HasContainers(path)) return path;

        var found = FindContainerDirectories(path).ToList();

        return found.Count switch
        {
            1 => found[0],
            0 => throw new UserFacingException(
                $"No .utoc, .ucas or .pak files were found under {path}.",
                "Unreal games keep them in <Game>/Content/Paks. Point --paks at that folder."),
            _ => throw new UserFacingException(
                $"Several container folders were found under {path}:",
                string.Join(Environment.NewLine, found.Select(d => "  " + d)) +
                Environment.NewLine + "Pass the one you want with --paks.")
        };
    }

    /// <summary>
    /// Looks for a single .usmap next to the executable, in the working directory, beside the
    /// containers or one level above them. Silent when it finds nothing: the caller decides
    /// whether mappings are required for the requested mode.
    /// </summary>
    public static string? FindMappings(string paksDirectory, string? outputDirectory)
    {
        var candidates = new List<string>();

        foreach (var directory in MappingSearchDirectories(paksDirectory, outputDirectory))
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) continue;

            try { candidates.AddRange(Directory.EnumerateFiles(directory, "*.usmap", SearchOption.TopDirectoryOnly)); }
            catch (UnauthorizedAccessException) { /* not ours to read, keep looking */ }
        }

        var unique = candidates
            .DistinctBy(p => Path.GetFullPath(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return unique.Count switch
        {
            1 => unique[0],
            0 => null,
            // Several mappings files usually means several game versions side by side. Guessing
            // would silently produce garbage, so make the user choose.
            _ => throw new UserFacingException(
                "Found more than one .usmap file, so it is not clear which one to use:",
                string.Join(Environment.NewLine, unique.Select(p => "  " + p)) +
                Environment.NewLine + "Pick one with --usmap.")
        };
    }

    private static IEnumerable<string> MappingSearchDirectories(string paksDirectory, string? outputDirectory)
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
        yield return paksDirectory;
        yield return Path.GetDirectoryName(paksDirectory) ?? "";
        if (!string.IsNullOrEmpty(outputDirectory)) yield return outputDirectory;
    }

    private static bool HasContainers(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(f => ContainerExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Breadth-first walk so a shallow, obvious hit wins over a deeply nested one.</summary>
    private static IEnumerable<string> FindContainerDirectories(string root)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth > MaxSearchDepth) continue;

            if (depth > 0 && HasContainers(current))
            {
                // Containers never nest, so stop descending this branch.
                yield return current;
                continue;
            }

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var child in children) queue.Enqueue((child, depth + 1));
        }
    }
}
