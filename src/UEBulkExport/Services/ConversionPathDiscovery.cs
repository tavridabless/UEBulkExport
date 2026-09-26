using Microsoft.Win32;

namespace UEBulkExport.Gui.Services;

/// <summary>Finds locally installed conversion tools and recently modified Unreal projects.</summary>
public static class ConversionPathDiscovery
{
    private static readonly string[] SkippedDirectories =
    [
        ".git", ".vs", "Binaries", "DerivedDataCache", "Intermediate", "node_modules", "Saved"
    ];

    public static ConversionPathDiscoveryResult Find(
        string? currentUModel,
        string? currentProject,
        string? currentEditor,
        bool searchProjects = true)
    {
        // Searching for projects is opt-in: the chosen project is where imported assets are saved.
        var project = ExistingFile(currentProject, ".uproject") ?? (searchProjects ? FindProject() : null);
        var engineAssociation = ReadEngineAssociation(project);

        return new ConversionPathDiscoveryResult(
            ExistingFile(currentUModel, ".exe") ?? FindUModel(),
            project,
            ExistingFile(currentEditor, ".exe") ?? FindEditor(engineAssociation, project),
            engineAssociation);
    }

    private static string? FindUModel()
    {
        var names = new[] { "umodel_64.exe", "umodel.exe" };
        var candidates = new List<string>();

        foreach (var directory in ExecutableSearchDirectories())
            candidates.AddRange(names.Select(name => Path.Combine(directory, name)));

        foreach (var root in FixedDriveRoots())
        {
            foreach (var relative in new[] { "umodel", "UEViewer", "UE Viewer", "Tools\\umodel" })
                candidates.AddRange(names.Select(name => Path.Combine(root, relative, name)));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindProject()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfDirectory(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Unreal Projects"));

        foreach (var drive in FixedDriveRoots())
        {
            foreach (var relative in new[]
                     {
                         "UNREAL\\Project", "UNREAL\\Projects", "Unreal Projects", "Unreal\\Projects",
                         "Projects"
                     })
                AddIfDirectory(roots, Path.Combine(drive, relative));
        }

        return roots.SelectMany(root => EnumerateFiles(root, "*.uproject", maxDepth: 4))
            .OrderByDescending(SafeLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? FindEditor(string engineAssociation, string? project)
    {
        var registered = FindRegisteredEditor(engineAssociation);
        if (registered is not null) return registered;

        var candidates = new List<string>();
        var versionFolder = engineAssociation.Length > 0 && char.IsDigit(engineAssociation[0])
            ? $"UE_{engineAssociation}"
            : null;

        foreach (var drive in FixedDriveRoots())
        {
            foreach (var baseRelative in new[] { "UNREAL", "Epic Games", "Unreal", "UnrealEngine" })
            {
                var baseDirectory = Path.Combine(drive, baseRelative);
                if (versionFolder is not null)
                    candidates.Add(EditorExecutable(Path.Combine(baseDirectory, versionFolder)));

                if (!Directory.Exists(baseDirectory)) continue;
                try
                {
                    candidates.AddRange(Directory.EnumerateDirectories(baseDirectory, "UE_*", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(EditorExecutable));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            var epic = Path.Combine(programFiles, "Epic Games");
            if (versionFolder is not null) candidates.Insert(0, EditorExecutable(Path.Combine(epic, versionFolder)));
        }

        if (!string.IsNullOrWhiteSpace(project))
        {
            var directory = Path.GetDirectoryName(project);
            for (var i = 0; i < 5 && directory is not null; i++, directory = Path.GetDirectoryName(directory))
            {
                if (versionFolder is not null)
                    candidates.Insert(0, EditorExecutable(Path.Combine(directory, versionFolder)));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindRegisteredEditor(string association)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(association)) return null;

        try
        {
            if (char.IsDigit(association[0]))
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\EpicGames\Unreal Engine\{association}");
                if (key?.GetValue("InstalledDirectory") is string directory)
                {
                    var editor = EditorExecutable(directory);
                    if (File.Exists(editor)) return editor;
                }
            }

            using var builds = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            if (builds?.GetValue(association) is string customDirectory)
            {
                var editor = EditorExecutable(customDirectory);
                if (File.Exists(editor)) return editor;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        return null;
    }

    private static string ReadEngineAssociation(string? project)
    {
        if (!File.Exists(project)) return "";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(project));
            return document.RootElement.TryGetProperty("EngineAssociation", out var property)
                ? property.GetString()?.Trim() ?? ""
                : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return "";
        }
    }

    private static IEnumerable<string> ExecutableSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && directory is not null; i++, directory = Path.GetDirectoryName(directory))
            if (seen.Add(directory)) yield return directory;

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (seen.Add(entry)) yield return entry;
    }

    private static IEnumerable<string> FixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern, int maxDepth)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;

            if (depth >= maxDepth) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
                if (!SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                    pending.Push((child, depth + 1));
        }
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static string EditorExecutable(string engineRoot) =>
        Path.Combine(engineRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");

    private static string? ExistingFile(string? path, string extension) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(path)
            : null;

    private static void AddIfDirectory(ISet<string> paths, string path)
    {
        if (Directory.Exists(path)) paths.Add(path);
    }
}

public sealed record ConversionPathDiscoveryResult(
    string? UModelPath,
    string? ProjectPath,
    string? UnrealEditorPath,
    string EngineAssociation);
