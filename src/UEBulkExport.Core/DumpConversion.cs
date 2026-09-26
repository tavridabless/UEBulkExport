using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UEBulkExport;

/// <summary>
/// Rebuilds the asset types that survive cooking by exporting them to interchange files with
/// UE Viewer and asking the destination Unreal Editor to import those files. This deliberately
/// does not rewrite cooked package headers: target packages are always created by the target editor.
/// </summary>
public sealed class DumpConversionService
{
    private static readonly HashSet<string> ImportableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gltf", ".glb", ".fbx", ".obj",
        ".png", ".tga", ".dds", ".jpg", ".jpeg", ".bmp", ".exr", ".hdr",
        ".wav", ".ogg", ".mp3"
    };

    private readonly object _processLock = new();
    private Process? _activeProcess;

    public async Task<DumpConversionSummary> RunAsync(
        DumpConversionOptions options,
        IProgress<DumpConversionProgress>? progress = null,
        Func<DumpConversionIssue, CancellationToken, Task<bool>>? issueHandler = null,
        CancellationToken ct = default)
    {
        Validate(options);

        var started = Stopwatch.StartNew();
        var source = Path.GetFullPath(options.SourceDirectory.Trim());
        var project = Path.GetFullPath(options.TargetProject.Trim());
        var projectDirectory = Path.GetDirectoryName(project)!;
        var destination = NormalizeDestination(options.DestinationPath);
        var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? DefaultWorkingDirectory(projectDirectory, source, options.SourceVersion)
            : Path.GetFullPath(options.WorkingDirectory.Trim());
        var exportedDirectory = Path.Combine(workingDirectory, "Exported");

        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(exportedDirectory);
        var errorReportPath = Path.Combine(workingDirectory, "conversion-errors.csv");
        if (File.Exists(errorReportPath)) File.Delete(errorReportPath);
        var failures = new List<DumpConversionFailure>();

        progress?.Report(new("Discovering", 0.05, "Scanning the dump for cooked packages."));
        var cookedPackages = Directory.EnumerateFiles(source, "*.uasset", SearchOption.AllDirectories).Count();
        var importRoot = source;

        if (cookedPackages > 0)
        {
            progress?.Report(new("Exporting", 0.15,
                $"UE Viewer is exporting {cookedPackages:N0} cooked packages."));

            var umodelLog = Path.Combine(workingDirectory, "umodel.log");
            if (File.Exists(umodelLog)) File.Delete(umodelLog);

            RecoverHiddenPackages(source);
            var hiddenPackages = new List<(string Original, string Hidden)>();
            try
            {
                foreach (var corrupt in FindPackagesWithEmptyPayloads(source))
                {
                    if (!TryHidePackage(corrupt.PackagePath, hiddenPackages)) continue;
                    failures.Add(new DumpConversionFailure(
                        Path.GetRelativePath(source, corrupt.PackagePath),
                        "UE Viewer preflight",
                        $"The required {Path.GetExtension(corrupt.PayloadPath)} payload is empty."));
                }

                var umodelArguments = BuildUModelArguments(options, source, exportedDirectory);
                while (true)
                {
                    try
                    {
                        await RunProcessAsync(options.UModelPath, umodelArguments,
                            Path.GetDirectoryName(Path.GetFullPath(options.UModelPath))!, umodelLog,
                            "UE Viewer", ct, appendLog: true);
                        break;
                    }
                    catch (UserFacingException e) when (issueHandler is not null)
                    {
                        var package = FindFailedPackage(umodelLog);
                        var issue = new DumpConversionIssue(
                            package ?? "UE Viewer batch",
                            e.Headline,
                            e.Hint ?? "See umodel.log for details.");
                        if (!await issueHandler(issue, ct)) throw new OperationCanceledException(ct);

                        if (package is null)
                        {
                            failures.Add(new DumpConversionFailure(
                                "UE Viewer batch", "UE Viewer", issue.Details));
                            break;
                        }

                        var physicalPackage = ResolvePackagePath(source, package);
                        if (physicalPackage is null ||
                            !TryHidePackage(physicalPackage, hiddenPackages))
                        {
                            failures.Add(new DumpConversionFailure(package, "UE Viewer", issue.Details));
                            break;
                        }

                        failures.Add(new DumpConversionFailure(package, "UE Viewer", issue.Details));
                        progress?.Report(new("Exporting", 0.15,
                            $"Skipped {package}; UE Viewer is continuing."));
                    }
                }
            }
            finally
            {
                RestoreHiddenPackages(hiddenPackages);
            }
            importRoot = exportedDirectory;
        }

        WriteFailureReport(errorReportPath, failures);

        progress?.Report(new("PreparingImport", 0.55, "Preparing files for the target Unreal Editor."));
        var files = EnumerateImportableFiles(importRoot).ToArray();
        if (files.Length == 0)
        {
            var actorX = CountActorXFiles(importRoot);
            var actorXHint = actorX > 0
                ? $" Found {actorX:N0} ActorX animation/mesh files (.psa/.psk), which Unreal Engine 5 cannot import natively."
                : "";
            throw new UserFacingException(
                "No files supported by the target Unreal Editor were produced.",
                "Check the source engine version and UE Viewer log." + actorXHint);
        }

        var manifestPath = Path.Combine(workingDirectory, "import-manifest.json");
        var scriptPath = Path.Combine(workingDirectory, "import-assets.py");
        var resultPath = Path.Combine(workingDirectory, "import-result.json");
        var manifest = files.Select(path => new ImportManifestEntry(
            path,
            BuildAssetDestination(importRoot, path, destination))).ToArray();

        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8, ct);
        await File.WriteAllTextAsync(scriptPath,
            BuildImportScript(manifestPath, resultPath, options.Overwrite), Encoding.UTF8, ct);
        if (File.Exists(resultPath)) File.Delete(resultPath);

        progress?.Report(new("Importing", 0.65,
            $"Unreal Editor is importing {files.Length:N0} interchange files."));

        var unrealArguments = BuildUnrealArguments(project, scriptPath);
        var unrealLog = Path.Combine(workingDirectory, "unreal-import.log");
        await RunProcessAsync(options.UnrealEditorPath, unrealArguments,
            Path.GetDirectoryName(Path.GetFullPath(options.UnrealEditorPath))!, unrealLog, "Unreal Editor", ct);

        if (!File.Exists(resultPath))
            throw new UserFacingException(
                "Unreal Editor finished without a conversion report.",
                "Enable the Python Editor Script Plugin in the target project and inspect unreal-import.log.");

        var result = JsonSerializer.Deserialize<ImportResult>(
            await File.ReadAllTextAsync(resultPath, ct), JsonOptions)
            ?? throw new UserFacingException("The Unreal Editor conversion report is invalid.");

        foreach (var failedFile in result.FailedFiles ?? [])
            failures.Add(new DumpConversionFailure(failedFile, "Unreal import",
                "The target Unreal Editor did not create an asset for this file."));
        WriteFailureReport(errorReportPath, failures);

        progress?.Report(new("Done", 1, "Dump conversion finished."));
        return new DumpConversionSummary(
            cookedPackages,
            files.Length,
            result.Imported,
            result.Failed,
            CountActorXFiles(importRoot),
            failures.Count,
            failures.Count > 0 ? errorReportPath : null,
            destination,
            workingDirectory,
            DetectTargetVersion(project, options.UnrealEditorPath),
            started.Elapsed);
    }

    public void Cancel()
    {
        lock (_processLock)
        {
            try
            {
                if (_activeProcess is { HasExited: false }) _activeProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between HasExited and Kill.
            }
        }
    }

    internal static void Validate(DumpConversionOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("Dump conversion currently requires Windows.");
        if (!Directory.Exists(options.SourceDirectory))
            throw new UserFacingException("The dump folder does not exist.");
        if (!File.Exists(options.UModelPath))
            throw new UserFacingException("UE Viewer (umodel) was not found.");
        if (!File.Exists(options.UnrealEditorPath))
            throw new UserFacingException("UnrealEditor-Cmd was not found.");
        if (!File.Exists(options.TargetProject) ||
            !Path.GetExtension(options.TargetProject).Equals(".uproject", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("Select a valid .uproject file.");
        _ = ToUModelTag(options.SourceVersion);
        _ = NormalizeDestination(options.DestinationPath);
    }

    internal static string ToUModelTag(string version)
    {
        var match = Regex.Match(version.Trim(), @"^(?:UE\s*)?(?<major>\d+)\.(?<minor>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, out var minor) || major != 4 || minor is < 0 or > 27)
            throw new UserFacingException(
                "UE Viewer supports this converter only for Unreal Engine 4.0 through 4.27 sources.",
                "Choose the exact version used to cook the source game.");
        return $"ue{major}.{minor}";
    }

    internal static string NormalizeDestination(string destination)
    {
        var value = destination.Trim().Replace('\\', '/').TrimEnd('/');
        if (!value.StartsWith("/Game", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("The target content path must start with /Game.");
        return value.Length == 5 ? "/Game" : value;
    }

    public static string DetectTargetVersion(string projectPath, string editorPath)
    {
        try
        {
            if (File.Exists(projectPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(projectPath));
                if (document.RootElement.TryGetProperty("EngineAssociation", out var association))
                {
                    var value = association.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^\d+\.\d+")) return value;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to the editor path; validation and Unreal itself provide the final error.
        }

        var pathMatch = Regex.Match(editorPath ?? "", @"UE[_ -](?<version>\d+\.\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return pathMatch.Success ? pathMatch.Groups["version"].Value : "unknown";
    }

    internal static IEnumerable<string> EnumerateImportableFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => ImportableExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    internal static string BuildAssetDestination(string importRoot, string sourceFile, string rootDestination)
    {
        var relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(importRoot, sourceFile)) ?? "";
        var components = relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizePathComponent)
            .Where(component => component.Length > 0);
        var suffix = string.Join('/', components);
        return suffix.Length == 0 ? rootDestination : $"{rootDestination}/{suffix}";
    }

    private static string SanitizePathComponent(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return builder.ToString().Trim('_');
    }

    private static int CountActorXFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Count(path => Path.GetExtension(path).Equals(".psa", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".psk", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".pskx", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string PackagePath, string PayloadPath)> FindPackagesWithEmptyPayloads(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in new[] { "*.ubulk", "*.uexp" })
        {
            foreach (var payload in Directory.EnumerateFiles(root, extension, SearchOption.AllDirectories))
            {
                if (new FileInfo(payload).Length != 0) continue;
                var package = Path.ChangeExtension(payload, ".uasset");
                if (File.Exists(package) && seen.Add(package)) yield return (package, payload);
            }
        }
    }

    private static bool TryHidePackage(string package, ICollection<(string Original, string Hidden)> hiddenPackages)
    {
        if (!File.Exists(package)) return false;
        var hidden = package + ".uebskip";
        if (File.Exists(hidden)) return false;
        File.Move(package, hidden);
        hiddenPackages.Add((package, hidden));
        return true;
    }

    private static void RestoreHiddenPackages(IEnumerable<(string Original, string Hidden)> packages)
    {
        foreach (var (original, hidden) in packages.Reverse())
            if (File.Exists(hidden) && !File.Exists(original)) File.Move(hidden, original);
    }

    private static void RecoverHiddenPackages(string root)
    {
        foreach (var hidden in Directory.EnumerateFiles(root, "*.uasset.uebskip", SearchOption.AllDirectories))
        {
            var original = hidden[..^".uebskip".Length];
            if (!File.Exists(original)) File.Move(hidden, original);
        }
    }

    private static string? FindFailedPackage(string logPath)
    {
        if (!File.Exists(logPath)) return null;
        var matches = Regex.Matches(File.ReadAllText(logPath),
            @"\*{8}\s+(?<package>[^\r\n]+?\.uasset)\s+\*{8}", RegexOptions.CultureInvariant);
        return matches.Count == 0 ? null : matches[^1].Groups["package"].Value.Trim();
    }

    private static string? ResolvePackagePath(string source, string package)
    {
        var relative = package.Replace('/', Path.DirectorySeparatorChar);
        var direct = Path.GetFullPath(Path.Combine(source, relative));
        if (direct.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(direct)) return direct;

        var contentMarker = $"{Path.DirectorySeparatorChar}Content{Path.DirectorySeparatorChar}";
        var markerIndex = relative.IndexOf(contentMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0 && Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))
                .Equals("Content", StringComparison.OrdinalIgnoreCase))
        {
            var fromContent = Path.Combine(source, relative[(markerIndex + contentMarker.Length)..]);
            if (File.Exists(fromContent)) return Path.GetFullPath(fromContent);
        }

        var name = Path.GetFileName(relative);
        return Directory.EnumerateFiles(source, name, SearchOption.AllDirectories)
            .FirstOrDefault(candidate => candidate.EndsWith(relative, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteFailureReport(string path, IReadOnlyCollection<DumpConversionFailure> failures)
    {
        if (failures.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        var lines = new[] { "file,stage,error" }.Concat(
            failures.Select(failure => string.Join(',', Csv(failure.SourceFile), Csv(failure.Stage), Csv(failure.Error))));
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string DefaultWorkingDirectory(string projectDirectory, string source, string sourceVersion)
    {
        var sourceName = SanitizePathComponent(new DirectoryInfo(source).Name);
        var version = SanitizePathComponent(sourceVersion);
        return Path.Combine(projectDirectory, "Saved", "UEBulkExport", "DumpConversion", $"{sourceName}_{version}");
    }

    private static IReadOnlyList<string> BuildUModelArguments(
        DumpConversionOptions options, string source, string output)
    {
        List<string> arguments =
        [
            "-export",
            "-gltf",
            "-png",
            "-sounds"
        ];
        if (!options.Overwrite) arguments.Add("-nooverwrite");
        arguments.Add($"-path={source}");
        arguments.Add($"-game={ToUModelTag(options.SourceVersion)}");
        arguments.Add($"-out={output}");
        arguments.Add("*");
        return arguments;
    }

    private static IReadOnlyList<string> BuildUnrealArguments(string project, string script) =>
    [
        project,
        "-unattended",
        "-nop4",
        "-nosplash",
        "-stdout",
        "-FullStdOutLogOutput",
        $"-ExecutePythonScript={script}"
    ];

    private async Task RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string logPath,
        string displayName,
        CancellationToken ct,
        bool appendLog = false)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Process? started;
        try { started = Process.Start(start); }
        catch (Win32Exception e)
        {
            throw new UserFacingException($"Could not start {displayName}: {e.Message}");
        }

        using var process = started ?? throw new UserFacingException($"Could not start {displayName}.");
        lock (_processLock) _activeProcess = process;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var logText = stdout + (stderr.Length == 0 ? "" : Environment.NewLine + stderr);
            if (appendLog)
                await File.AppendAllTextAsync(logPath,
                    $"{Environment.NewLine}----- attempt {DateTime.Now:O} -----{Environment.NewLine}{logText}",
                    Encoding.UTF8, ct);
            else
                await File.WriteAllTextAsync(logPath, logText, Encoding.UTF8, ct);

            if (process.ExitCode != 0)
                throw new UserFacingException(
                    $"{displayName} failed (exit {process.ExitCode}).",
                    LastLines(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        finally
        {
            lock (_processLock)
                if (ReferenceEquals(_activeProcess, process)) _activeProcess = null;
        }
    }

    private static string BuildImportScript(string manifestPath, string resultPath, bool overwrite)
    {
        static string PythonString(string value) => JsonSerializer.Serialize(value);
        return $$"""
            import json
            import unreal

            manifest_path = {{PythonString(manifestPath)}}
            result_path = {{PythonString(resultPath)}}
            overwrite = {{(overwrite ? "True" : "False")}}

            with open(manifest_path, "r", encoding="utf-8-sig") as stream:
                manifest = json.load(stream)

            tasks = []
            for item in manifest:
                task = unreal.AssetImportTask()
                task.filename = item["SourceFile"]
                task.destination_path = item["DestinationPath"]
                task.automated = True
                task.replace_existing = overwrite
                task.replace_existing_settings = overwrite
                task.save = True
                tasks.append(task)

            unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)
            imported = sum(1 for task in tasks if len(task.imported_object_paths) > 0)
            failed_files = [task.filename for task in tasks if len(task.imported_object_paths) == 0]
            failed = len(failed_files)
            result = {"Imported": imported, "Failed": failed, "FailedFiles": failed_files}
            with open(result_path, "w", encoding="utf-8") as stream:
                json.dump(result, stream, indent=2)
            unreal.log("UEBulkExport dump conversion: imported={}, failed={}".format(imported, failed))
            """;
    }

    private static string LastLines(string output) =>
        string.Join(Environment.NewLine, output.Split('\n').TakeLast(12)).Trim();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed record ImportManifestEntry(string SourceFile, string DestinationPath);
    private sealed record ImportResult(int Imported, int Failed, string[]? FailedFiles = null);
}

public sealed record DumpConversionOptions(
    string SourceDirectory,
    string SourceVersion,
    string UModelPath,
    string TargetProject,
    string UnrealEditorPath,
    string DestinationPath,
    string? WorkingDirectory = null,
    bool Overwrite = false);

public sealed record DumpConversionProgress(string Stage, double Fraction, string Message);

public sealed record DumpConversionIssue(string SourceFile, string Headline, string Details);

public sealed record DumpConversionFailure(string SourceFile, string Stage, string Error);

public sealed record DumpConversionSummary(
    int CookedPackages,
    int ImportableFiles,
    int ImportedFiles,
    int FailedFiles,
    int UnsupportedActorXFiles,
    int SkippedFiles,
    string? ErrorReportPath,
    string DestinationPath,
    string WorkingDirectory,
    string TargetVersion,
    TimeSpan Elapsed);
