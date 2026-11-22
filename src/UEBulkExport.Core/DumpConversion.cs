using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UEBulkExport;

/// <summary>
/// Rebuilds the asset types that survive cooking by exporting them to interchange files with
/// UE Viewer for UE4 or CUE4Parse for UE5, then asking the destination Unreal Editor to import
/// them. This deliberately
/// does not rewrite cooked package headers: target packages are always created by the target editor.
/// The source dump is only ever read: packages UE Viewer must skip are left out of a hard-linked
/// working copy instead of being renamed in place.
/// </summary>
public sealed class DumpConversionService
{
    private static readonly HashSet<string> ImportableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gltf", ".glb", ".fbx", ".obj",
        ".png", ".tga", ".dds", ".jpg", ".jpeg", ".bmp", ".exr", ".hdr",
        ".wav", ".ogg", ".mp3"
    };

    private static readonly EnumerationOptions DumpEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary>UE Viewer prints a line per package; this long without one means it is stuck.</summary>
    public static readonly TimeSpan DefaultUModelInactivityTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The editor logs continuously while it imports; half an hour of silence means it hangs on a
    /// dialog, a crash reporter or a stuck importer rather than doing work.
    /// </summary>
    public static readonly TimeSpan DefaultEditorInactivityTimeout = TimeSpan.FromMinutes(30);

    private readonly object _processLock = new();
    private Process? _activeProcess;

    /// <summary>
    /// Reads source metadata and the target descriptor to build a plan. It never creates work
    /// folders, recovers hidden files, downloads tools or starts Unreal Editor / UE Viewer.
    /// </summary>
    public Task<DumpConversionPreflight> PreflightAsync(DumpConversionOptions options, CancellationToken ct = default) =>
        Task.Run(() => InspectPreflight(options, ct), ct);

    private static DumpConversionPreflight InspectPreflight(DumpConversionOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var diagnostics = new List<DumpConversionDiagnostic>();
        void Error(string code, string? detail = null) => diagnostics.Add(new(DumpConversionDiagnosticLevel.Error, code, detail));
        void Warning(string code, string? detail = null) => diagnostics.Add(new(DumpConversionDiagnosticLevel.Warning, code, detail));

        var source = options.SourceDirectory.Trim().Trim('"');
        var project = options.TargetProject.Trim().Trim('"');
        var editor = options.UnrealEditorPath.Trim().Trim('"');
        var packages = 0;
        var maps = 0;
        var payloads = 0;
        var emptyPayloads = 0;
        var importable = 0;
        var actorX = 0;
        var hidden = 0;
        long bytes = 0;
        var targetVersion = DetectTargetVersion(project, editor);
        var workingDirectory = "";
        var destination = options.DestinationPath.Trim();

        if (!OperatingSystem.IsWindows()) Error("PlatformUnsupported");
        var ue5 = false;
        try
        {
            ue5 = IsUE5Source(options.SourceVersion);
            if (ue5) _ = Cli.ParseGame(options.SourceVersion);
        }
        catch (UserFacingException) { Error("SourceVersionInvalid", options.SourceVersion); }

        try { destination = NormalizeDestination(options.DestinationPath); }
        catch (UserFacingException) { Error("DestinationInvalid"); }

        if (!Directory.Exists(source)) Error("SourceMissing");
        else
        {
            try
            {
                foreach (var file in EnumerateDumpFiles(source))
                {
                    ct.ThrowIfCancellationRequested();
                    var extension = Path.GetExtension(file);
                    var length = new FileInfo(file).Length;
                    bytes = checked(bytes + length);
                    if (extension.Equals(".uasset", StringComparison.OrdinalIgnoreCase)) packages++;
                    else if (extension.Equals(".umap", StringComparison.OrdinalIgnoreCase)) maps++;
                    else if (extension.Equals(".uexp", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".ubulk", StringComparison.OrdinalIgnoreCase))
                    {
                        payloads++;
                        if (length == 0) emptyPayloads++;
                    }
                    if (ImportableExtensions.Contains(extension)) importable++;
                    if (extension.Equals(".psk", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".pskx", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".psa", StringComparison.OrdinalIgnoreCase)) actorX++;
                    if (file.EndsWith(".uasset.uebskip", StringComparison.OrdinalIgnoreCase)) hidden++;
                }
                if (packages == 0 && importable == 0 && hidden == 0) Error("NoImportableSource");
                if (emptyPayloads > 0) Warning("EmptyPayloads", emptyPayloads.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (actorX > 0) Warning("ActorXUnsupported", actorX.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (hidden > 0) Warning("HiddenPackages", hidden.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OverflowException)
            {
                Error("SourceUnreadable", e.Message);
            }
        }

        if (!File.Exists(project) || !Path.GetExtension(project).Equals(".uproject", StringComparison.OrdinalIgnoreCase))
            Error("ProjectMissing");
        else
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(File.ReadAllText(project));
                if (document.RootElement.ValueKind != JsonValueKind.Object) Error("ProjectInvalid");
                else
                {
                    bool? pythonEnabled = null;
                    if (document.RootElement.TryGetProperty("Plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var plugin in plugins.EnumerateArray())
                        {
                            if (plugin.ValueKind != JsonValueKind.Object ||
                                !plugin.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String ||
                                !string.Equals(name.GetString(), "PythonScriptPlugin", StringComparison.OrdinalIgnoreCase)) continue;
                            if (plugin.TryGetProperty("Enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                                pythonEnabled = enabled.GetBoolean();
                        }
                    }
                    if (pythonEnabled == false) Error("PythonDisabled");
                    else if (pythonEnabled is null) Warning("PythonUnconfirmed");
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                Error("ProjectUnreadable", e.Message);
            }
        }

        if (!File.Exists(editor)) Error("EditorMissing");
        if (!ue5 && !File.Exists(options.UModelPath.Trim().Trim('"'))) Error("UModelMissing");
        if (ue5)
        {
            if (string.IsNullOrWhiteSpace(options.MappingsPath)) Warning("MappingsUnconfirmed");
            else if (!File.Exists(options.MappingsPath.Trim().Trim('"'))) Error("MappingsMissing");
        }
        if (targetVersion == "unknown") Warning("TargetVersionUnknown");
        // A recognized version routes the parsers; it is not evidence that this particular cooked
        // dump can be rebuilt. The plan never claims verification of newer or game-specific formats.
        Warning("VersionCompatibilityUnverified");
        if (packages > 0 || maps > 0 || hidden > 0) Warning("CookedLimitations");
        if (options.Overwrite) Warning("Overwrite", destination);
        else if (options.ReplaceChanged) Warning("ReplaceChanged", destination);

        try
        {
            if (File.Exists(project) && Directory.Exists(source))
            {
                workingDirectory = GetWorkingDirectory(Path.GetFullPath(source), Path.GetFullPath(project), options);
            }
        }
        catch (UserFacingException) { Error("WorkingPathOverlap"); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Error("WorkingPathInvalid", e.Message);
        }
        ct.ThrowIfCancellationRequested();
        return new DumpConversionPreflight(packages, maps, payloads, emptyPayloads, importable, actorX,
            hidden, bytes, targetVersion, destination, workingDirectory, diagnostics.AsReadOnly());
    }

    public async Task<DumpConversionSummary> RunAsync(
        DumpConversionOptions options,
        IProgress<DumpConversionProgress>? progress = null,
        Func<DumpConversionIssue, CancellationToken, Task<bool>>? issueHandler = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        options = options with
        {
            SourceDirectory = options.SourceDirectory.Trim().Trim('"'),
            TargetProject = options.TargetProject.Trim().Trim('"'),
            UnrealEditorPath = options.UnrealEditorPath.Trim().Trim('"'),
            UModelPath = options.UModelPath.Trim().Trim('"'),
            MappingsPath = options.MappingsPath?.Trim().Trim('"')
        };
        Validate(options);

        var started = Stopwatch.StartNew();
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.SourceDirectory));
        var project = Path.GetFullPath(options.TargetProject);
        var destination = NormalizeDestination(options.DestinationPath);
        var workingDirectory = GetWorkingDirectory(source, project, options);
        var exportRoot = Path.Combine(workingDirectory, "Exported");

        Directory.CreateDirectory(workingDirectory);
        using var workspaceLock = LockWorkingDirectory(workingDirectory);
        var errorReportPath = Path.Combine(workingDirectory, "conversion-errors.csv");
        if (File.Exists(errorReportPath)) File.Delete(errorReportPath);
        var failures = new List<DumpConversionFailure>();

        progress?.Report(new("Discovering", 0.05, "Scanning the dump for cooked packages."));

        // Builds before this one renamed packages inside the dump; put back anything a crash left behind.
        await Task.Run(() => RecoverHiddenPackages(source, ct), ct);
        var cookedInputs = await Task.Run(() => CollectCookedInputs(source, ct), ct);
        var cookedPackages = cookedInputs.Count(path => Path.GetExtension(path).Equals(".uasset",
            StringComparison.OrdinalIgnoreCase));
        var importRoot = source;
        string? cookedStage = null;

        if (cookedPackages > 0)
        {
            progress?.Report(new("Fingerprinting", 0.08, "Checking whether the cooked source changed."));
            var exportedDirectory = await DumpConversionFreshness.OpenCookedStageAsync(exportRoot, source,
                cookedInputs, options, ct);
            cookedStage = exportedDirectory;
            if (IsUE5Source(options.SourceVersion))
            {
                await ExportUE5Async(options, source, exportedDirectory, failures, progress, ct);
                importRoot = exportedDirectory;
            }
            else
            {
                progress?.Report(new("Exporting", 0.15,
                    $"UE Viewer is exporting {cookedPackages:N0} cooked packages."));

                var umodelLog = Path.Combine(workingDirectory, "umodel.log");
                if (File.Exists(umodelLog)) File.Delete(umodelLog);

                // Relative paths of packages UE Viewer must not see.
                var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var corrupt in await Task.Run(() => FindPackagesWithEmptyPayloads(source, ct).ToList(), ct))
                {
                    var relative = Path.GetRelativePath(source, corrupt.PackagePath);
                    if (!skipped.Add(relative)) continue;
                    failures.Add(new DumpConversionFailure(relative, "UE Viewer preflight",
                        $"The required {Path.GetExtension(corrupt.PayloadPath)} payload is empty."));
                }

                SourceMirror? mirror = null;
                try
                {
                    if (skipped.Count > 0)
                        mirror = await CreateMirrorAsync(source, workingDirectory, skipped, progress, ct);

                    while (true)
                    {
                        var umodelSource = mirror?.Root ?? source;
                        var logOffset = File.Exists(umodelLog) ? new FileInfo(umodelLog).Length : 0;
                        try
                        {
                            await RunProcessAsync(options.UModelPath,
                                start => AddArguments(start, BuildUModelArguments(options, umodelSource, exportedDirectory)),
                                Path.GetDirectoryName(Path.GetFullPath(options.UModelPath))!, umodelLog,
                                "UE Viewer", options.UModelInactivityTimeout ?? DefaultUModelInactivityTimeout,
                                ct, appendLog: true);
                            break;
                        }
                        catch (UserFacingException e) when (issueHandler is not null)
                        {
                            // Only this attempt's output counts: an earlier banner names a package that is
                            // already excluded, and retrying it would loop.
                            var package = FindFailedPackage(umodelLog, logOffset);
                            var issue = new DumpConversionIssue(
                                package ?? "UE Viewer batch",
                                e.Headline,
                                e.Hint ?? "See umodel.log for details.");
                            if (!await issueHandler(issue, ct)) throw new OperationCanceledException(ct);

                            var physical = package is null ? null : await Task.Run(() => ResolvePackagePath(umodelSource, package), ct);
                            var relative = physical is null ? null : Path.GetRelativePath(umodelSource, physical);
                            failures.Add(new DumpConversionFailure(package ?? "UE Viewer batch", "UE Viewer", issue.Details));
                            if (relative is null || !skipped.Add(relative)) break;

                            if (mirror is null)
                                mirror = await CreateMirrorAsync(source, workingDirectory, skipped, progress, ct);
                            else
                                mirror.Exclude(relative);

                            progress?.Report(new("Exporting", 0.15, $"Skipped {package}; UE Viewer is continuing."));
                        }
                    }
                }
                finally
                {
                    mirror?.Dispose();
                }

                importRoot = exportedDirectory;
            }
        }

        // Sound left in its original encoding is simply not picked up by the import below; without
        // a row here it would vanish from the result with nothing to say why.
        failures.AddRange(await Task.Run(() => FindUnconvertedAudio(importRoot).ToList(), ct));
        WriteFailureReport(errorReportPath, failures);

        progress?.Report(new("PreparingImport", 0.55, "Preparing files for the target Unreal Editor."));
        var files = await Task.Run(() => EnumerateImportableFiles(importRoot).ToArray(), ct);
        if (files.Length == 0)
        {
            var actorX = CountActorXFiles(importRoot);
            var actorXHint = actorX > 0
                ? $" Found {actorX:N0} ActorX animation/mesh files (.psa/.psk), which Unreal Engine 5 cannot import natively."
                : "";
            var sourceHint = IsUE5Source(options.SourceVersion)
                ? "Inspect the CUE4Parse errors.csv in the working folder. If no mappings were selected, retry with a .usmap from the same game build."
                : "Check the source engine version and UE Viewer log.";
            throw new UserFacingException(
                "No files supported by the target Unreal Editor were produced.",
                sourceHint + actorXHint);
        }

        var manifestPath = Path.Combine(workingDirectory, "import-manifest.json");
        var scriptPath = Path.Combine(workingDirectory, "import-assets.py");
        var resultPath = Path.Combine(workingDirectory, "import-result.json");
        var ledgerPath = Path.Combine(workingDirectory, "import-ledger.json");
        var manifest = new List<ImportManifestEntry>(files.Length);
        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(importRoot, path);
            var logicalSource = cookedStage is null ? $"interchange:{path}" : $"cooked:{source}|{relative}";
            var sourceKey = $"{project}|{logicalSource}".ToUpperInvariant();
            var legacyPath = cookedStage is null ? path : Path.Combine(exportRoot, relative);
            var fingerprint = await DumpConversionFreshness.FingerprintImportAsync(path, importRoot, ct);
            manifest.Add(new(path, BuildAssetDestination(importRoot, path, destination), sourceKey, legacyPath,
                fingerprint));
        }

        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8, ct);
        await File.WriteAllTextAsync(scriptPath,
            BuildImportScript(manifestPath, resultPath, ledgerPath, options.Overwrite, options.ReplaceChanged),
            Encoding.UTF8, ct);
        if (File.Exists(resultPath)) File.Delete(resultPath);

        progress?.Report(new("Importing", 0.65,
            $"Unreal Editor is importing {files.Length:N0} interchange files."));

        var unrealLog = Path.Combine(workingDirectory, "unreal-import.log");
        var commandLine = BuildUnrealCommandLine(project, scriptPath);
        await RunProcessAsync(options.UnrealEditorPath, start => start.Arguments = commandLine,
            Path.GetDirectoryName(Path.GetFullPath(options.UnrealEditorPath))!, unrealLog, "Unreal Editor",
            options.EditorInactivityTimeout ?? DefaultEditorInactivityTimeout, ct);

        if (!File.Exists(resultPath))
        {
            // The editor exits with 0 even when the script fails, so the log is the only witness.
            var pythonErrors = ReadPythonErrors(unrealLog);
            throw new UserFacingException(
                "Unreal Editor finished without a conversion report.",
                pythonErrors.Length > 0
                    ? string.Join(Environment.NewLine, pythonErrors) + Environment.NewLine + "See unreal-import.log."
                    : "Enable the Python Editor Script Plugin in the target project and inspect unreal-import.log.");
        }

        var result = JsonSerializer.Deserialize<ImportResult>(
            await File.ReadAllTextAsync(resultPath, ct), JsonOptions)
            ?? throw new UserFacingException("The Unreal Editor conversion report is invalid.");

        foreach (var failedFile in result.FailedFiles ?? [])
            failures.Add(new DumpConversionFailure(failedFile, "Unreal import",
                "The target Unreal Editor did not create an asset for this file."));
        foreach (var staleFile in result.StaleFiles ?? [])
            failures.Add(new DumpConversionFailure(staleFile, "Unreal import",
                "The source changed. The existing target was left intact. Allow replacing changed assets to update it."));
        WriteFailureReport(errorReportPath, failures);
        if (cookedStage is not null && result.Failed == 0) DumpConversionFreshness.PruneCookedStages(cookedStage);

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
            started.Elapsed,
            result.AlreadyPresent,
            result.StaleFiles?.Length ?? 0);
    }

    public void Cancel()
    {
        lock (_processLock)
        {
            if (_activeProcess is { } process) Kill(process);
        }
    }

    internal static void Validate(DumpConversionOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("Dump conversion currently requires Windows.");
        if (!Directory.Exists(options.SourceDirectory))
            throw new UserFacingException("The dump folder does not exist.");
        var isUE5 = IsUE5Source(options.SourceVersion);
        if (!isUE5 && !File.Exists(options.UModelPath))
            throw new UserFacingException("UE Viewer (umodel) was not found.");
        if (isUE5 && !string.IsNullOrWhiteSpace(options.MappingsPath) && !File.Exists(options.MappingsPath))
            throw new UserFacingException(
                "The selected mappings (.usmap) file does not exist.",
                "Choose an existing mappings file, or clear the field to try the UE5 export without mappings.");
        if (!File.Exists(options.UnrealEditorPath))
            throw new UserFacingException("UnrealEditor-Cmd was not found.");
        if (!File.Exists(options.TargetProject) ||
            !Path.GetExtension(options.TargetProject).Equals(".uproject", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("Select a valid .uproject file.");
        _ = ParseSourceVersion(options.SourceVersion);
        _ = NormalizeDestination(options.DestinationPath);
    }

    internal static string ToUModelTag(string version)
    {
        var (major, minor) = ParseSourceVersion(version);
        if (major != 4)
            throw new UserFacingException(
                "UE Viewer supports this converter only for Unreal Engine 4.0 through 4.27 sources.",
                "Choose the exact version used to cook the source game.");
        return $"ue{major}.{minor}";
    }

    public static bool IsUE5Source(string version) => ParseSourceVersion(version).Major == 5;

    internal static (int Major, int Minor) ParseSourceVersion(string version)
    {
        var match = Regex.Match(version.Trim(), @"^(?:UE\s*)?(?<major>\d+)\.(?<minor>\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, out var minor) ||
            (major == 4 && minor is < 0 or > 27) || (major == 5 && minor is < 0 or > 8) ||
            major is not (4 or 5))
            throw new UserFacingException(
                "This converter supports Unreal Engine 4.0–4.27 and 5.0–5.8 source dumps.",
                "Choose the exact version used to cook the source game.");
        return (major, minor);
    }

    private static async Task ExportUE5Async(
        DumpConversionOptions conversion,
        string source,
        string output,
        ICollection<DumpConversionFailure> failures,
        IProgress<DumpConversionProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new("Exporting", 0.15,
            "CUE4Parse is rebuilding supported assets from the Unreal Engine 5 dump."));

        var options = CreateUE5ExportOptions(conversion, source, output);
        Natives.Initialize(options);

        using var exporter = new BulkExporter(options);
        exporter.ProgressChanged += p => progress?.Report(new DumpConversionProgress(
            "Exporting", 0.15 + p.Fraction * 0.4,
            $"CUE4Parse processed {p.Processed:N0} of {p.Total:N0} entries."));
        exporter.Mount();
        var files = exporter.SelectFiles();
        // Without mappings this is deliberately best effort: versioned packages and data that
        // CUE4Parse can understand are still exported, while failures land in the stage's errors.csv.
        // With mappings, keep the fast preflight so a wrong file fails before a long run.
        if (options.UsmapPath is not null) exporter.VerifyMappings(files);
        var summary = await exporter.RunAsync(files, ct);

        if (summary.Cancelled || ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        var failed = summary.FailedEntries + summary.FailedObjects;
        if (failed > 0)
            failures.Add(new DumpConversionFailure(
                Path.Combine(output, "errors.csv"),
                "CUE4Parse export",
                $"{failed:N0} package or object export(s) failed; see the listed CUE4Parse report."));
    }

    internal static Options CreateUE5ExportOptions(DumpConversionOptions conversion, string source, string output) =>
        new()
        {
            PaksDirectory = source,
            OutputDirectory = output,
            Game = Cli.ParseGame(conversion.SourceVersion),
            Mode = ExportMode.Full,
            UsmapPath = string.IsNullOrWhiteSpace(conversion.MappingsPath)
                ? null
                : conversion.MappingsPath.Trim(),
            // UE5 sound is usually Bink, which the editor cannot import; vgmstream adds the .wav it
            // can. A stale path from settings is dropped rather than failing on every sound.
            VgmStreamPath = !string.IsNullOrWhiteSpace(conversion.VgmStreamPath) && File.Exists(conversion.VgmStreamPath.Trim())
                ? conversion.VgmStreamPath.Trim()
                : null,
            WriteJson = false,
            WriteAssets = true,
            WriteRawMisc = false,
            WriteRawPackages = false,
            ExportMaterials = false,
            ExportWorlds = false,
            Resume = !conversion.Overwrite
        };

    internal static string NormalizeDestination(string destination)
    {
        var value = destination.Trim().Replace('\\', '/').TrimEnd('/');
        if (!IsGameDestination(value))
            throw new UserFacingException("The target content path is invalid.",
                "Use /Game or a valid content folder such as /Game/Migrated.");
        return "/Game" + value[5..];
    }

    public static bool IsValidDestination(string destination) =>
        IsGameDestination(destination.Trim().Replace('\\', '/').TrimEnd('/'));

    private static bool IsGameDestination(string value)
    {
        if (value.Equals("/Game", StringComparison.OrdinalIgnoreCase)) return true;
        if (!value.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return false;

        // Unreal's long package names forbid these characters in content path segments.
        return !value.Contains("//", StringComparison.Ordinal) &&
               !value.Any(c => char.IsControl(c) || ":*?\"<>|' ,.&!~@#".Contains(c));
    }

    public static string DetectTargetVersion(string projectPath, string editorPath)
    {
        try
        {
            if (File.Exists(projectPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(projectPath));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("EngineAssociation", out var association) &&
                    association.ValueKind == JsonValueKind.String)
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
        EnumerateDumpFiles(root)
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

    /// <summary>
    /// The editor's command line. <c>-ExecutePythonScript</c> takes its value through Unreal's own
    /// parser, which reads backslashes as escapes (<c>\U</c>, <c>\6</c> vanish) and stops at a space
    /// unless the quote starts right after the equals sign. Forward slashes and value-only quoting
    /// survive both; .NET's ArgumentList would quote the whole switch and lose the path.
    /// </summary>
    internal static string BuildUnrealCommandLine(string project, string script) =>
        $"{Quote(project)} -unattended -nop4 -nosplash -stdout -FullStdOutLogOutput " +
        $"-ExecutePythonScript={Quote(script.Replace('\\', '/'))}";

    // Windows paths cannot contain quotes, so wrapping is enough.
    private static string Quote(string value) => $"\"{value}\"";

    private static IEnumerable<string> EnumerateDumpFiles(string root, string pattern = "*") =>
        Directory.EnumerateFiles(root, pattern, DumpEnumeration);

    private static List<string> CollectCookedInputs(string root, CancellationToken ct)
    {
        var inputs = new List<string>();
        foreach (var path in EnumerateDumpFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetExtension(path).ToLowerInvariant() is ".uasset" or ".umap" or ".uexp" or ".ubulk" or
                ".uptnl" or ".tfc" or ".pak" or ".utoc" or ".ucas") inputs.Add(path);
        }
        return inputs;
    }

    private static bool IsInsideOrSame(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string GetWorkingDirectory(string source, string project, DumpConversionOptions options)
    {
        var work = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? DefaultWorkingDirectory(Path.GetDirectoryName(project)!, source, options.SourceVersion)
            : Path.GetFullPath(options.WorkingDirectory.Trim().Trim('"'));
        if (IsInsideOrSame(work, source) || IsInsideOrSame(source, work))
            throw new UserFacingException("The migration working folder overlaps the dump folder.",
                "Choose a separate working folder, or move the dump outside the working folder.");
        return work;
    }

    private static FileStream LockWorkingDirectory(string directory)
    {
        try
        {
            return new FileStream(Path.Combine(directory, ".conversion.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException e)
        {
            throw new UserFacingException("Could not reserve the migration working folder.",
                $"Check that it is writable and that another migration is not using it. {e.Message}");
        }
    }

    internal static string DefaultWorkingDirectory(string projectDirectory, string source, string sourceVersion)
    {
        var sourceName = SanitizePathComponent(new DirectoryInfo(source).Name);
        var version = SanitizePathComponent(sourceVersion);

        // Two dumps may share a folder name; their exports must not mix. Case only tells folders
        // apart where the file system does.
        var identity = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..8].ToLowerInvariant();

        return Path.Combine(projectDirectory, "Saved", "UEBulkExport", "DumpConversion",
            $"{sourceName}_{version}_{hash}");
    }

    private static string SanitizePathComponent(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return builder.ToString().Trim('_');
    }

    /// <summary>Audio encodings the exporters write as-is and Unreal Editor cannot import.</summary>
    private static readonly string[] EncodedAudioExtensions = [".binka", ".adpcm", ".opus", ".rada", ".at9", ".xma"];

    /// <summary>
    /// Sounds that stayed in an encoding the editor cannot import because no .wav was produced next
    /// to them, which is what happens when vgmstream is not available during the export.
    /// </summary>
    internal static IEnumerable<DumpConversionFailure> FindUnconvertedAudio(string root)
    {
        foreach (var path in EnumerateDumpFiles(root))
        {
            if (!EncodedAudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.ChangeExtension(path, ".wav"))) continue;

            yield return new DumpConversionFailure(
                Path.GetRelativePath(root, path),
                "Audio",
                $"The sound was exported as {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()}, which Unreal Editor " +
                "cannot import. Set the vgmstream path in Settings and run the conversion again to get a .wav.");
        }
    }

    private static int CountActorXFiles(string root) =>
        EnumerateDumpFiles(root)
            .Count(path => Path.GetExtension(path).Equals(".psa", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".psk", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".pskx", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string PackagePath, string PayloadPath)> FindPackagesWithEmptyPayloads(
        string root, CancellationToken ct)
    {
        foreach (var package in EnumerateDumpFiles(root, "*.uasset"))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var extension in new[] { ".ubulk", ".uexp" })
            {
                var payload = new FileInfo(Path.ChangeExtension(package, extension));
                if (!payload.Exists || payload.Attributes.HasFlag(FileAttributes.ReparsePoint) || payload.Length != 0)
                    continue;
                yield return (package, payload.FullName);
                break;
            }
        }
    }

    /// <summary>Undoes the in-place renames earlier 2.1.0 builds used, if a crash left any behind.</summary>
    private static void RecoverHiddenPackages(string root, CancellationToken ct)
    {
        foreach (var hidden in EnumerateDumpFiles(root, "*.uasset.uebskip"))
        {
            ct.ThrowIfCancellationRequested();
            var original = hidden[..^".uebskip".Length];
            try
            {
                if (!File.Exists(original)) File.Move(hidden, original);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A read-only dump cannot hold such leftovers in the first place.
            }
        }
    }

    private static async Task<SourceMirror> CreateMirrorAsync(string source, string workingDirectory,
        IReadOnlySet<string> excluded, IProgress<DumpConversionProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("Mirroring", 0.12, "Preparing a working copy of the dump without the skipped packages."));
        return await Task.Run(() => SourceMirror.Create(source, workingDirectory, excluded, ct), ct);
    }

    internal static string? FindFailedPackage(string logPath, long fromOffset = 0)
    {
        if (!File.Exists(logPath)) return null;

        string text;
        using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.Seek(Math.Min(fromOffset, stream.Length), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }

        var matches = Regex.Matches(text,
            @"\*{8}\s+(?<package>[^\r\n]+?\.uasset)\s+\*{8}", RegexOptions.CultureInvariant);
        return matches.Count == 0 ? null : matches[^1].Groups["package"].Value.Trim();
    }

    private static string? ResolvePackagePath(string source, string package)
    {
        var relative = package.Replace('/', Path.DirectorySeparatorChar);
        var direct = FindPhysicalPackage(relative);
        if (direct is not null) return direct;

        var contentMarker = $"{Path.DirectorySeparatorChar}Content{Path.DirectorySeparatorChar}";
        var markerIndex = relative.IndexOf(contentMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0 && Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))
                .Equals("Content", StringComparison.OrdinalIgnoreCase))
        {
            var fromContent = FindPhysicalPackage(relative[(markerIndex + contentMarker.Length)..]);
            if (fromContent is not null) return fromContent;
        }

        return EnumerateDumpFiles(source, "*.uasset")
            .FirstOrDefault(candidate => candidate.EndsWith(relative, StringComparison.OrdinalIgnoreCase));

        string? FindPhysicalPackage(string path)
        {
            try
            {
                var physical = Path.GetFullPath(Path.Combine(source, path));
                return IsInsideOrSame(physical, source) && File.Exists(physical) ? physical : null;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
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

    // UE Viewer parses its switches from the C runtime's argv, where whole-argument quoting is right.
    private static void AddArguments(ProcessStartInfo start, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }

    private static string[] ReadPythonErrors(string logPath)
    {
        if (!File.Exists(logPath)) return [];
        return File.ReadLines(logPath)
            .Where(line => line.Contains("LogPython: Error", StringComparison.Ordinal) ||
                           line.Contains("LogEditorPythonExecuter: Error", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .Take(4)
            .ToArray();
    }

    /// <summary>
    /// Runs a tool to completion, streaming its output to <paramref name="logPath"/> as it arrives
    /// so a long import never piles up in memory and a cancelled run still leaves a log. A tool that
    /// prints nothing for <paramref name="inactivityTimeout"/> is treated as hung and stopped.
    /// </summary>
    private async Task RunProcessAsync(
        string executable,
        Action<ProcessStartInfo> setArguments,
        string workingDirectory,
        string logPath,
        string displayName,
        TimeSpan inactivityTimeout,
        CancellationToken ct,
        bool appendLog = false)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        setArguments(start);

        // Open the log before launching a child which would otherwise outlive a log-open failure.
        await using var log = new StreamWriter(logPath, appendLog, new UTF8Encoding(false)) { AutoFlush = true };
        if (appendLog) await log.WriteLineAsync($"----- attempt {DateTime.Now:O} -----");
        ct.ThrowIfCancellationRequested();

        Process? started;
        try { started = Process.Start(start); }
        catch (Win32Exception e)
        {
            throw new UserFacingException($"Could not start {displayName}: {e.Message}");
        }

        using var process = started ?? throw new UserFacingException($"Could not start {displayName}.");
        lock (_processLock) _activeProcess = process;

        var logGate = new object();
        var tail = new Queue<string>();
        var lastOutput = Environment.TickCount64;

        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (logGate)
                {
                    log.WriteLine(line);
                    tail.Enqueue(line);
                    if (tail.Count > 12) tail.Dequeue();
                }
                Interlocked.Exchange(ref lastOutput, Environment.TickCount64);
            }
        }

        var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));

        try
        {
            while (true)
            {
                using var slice = CancellationTokenSource.CreateLinkedTokenSource(ct);
                slice.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await process.WaitForExitAsync(slice.Token);
                    break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastOutput));
                    if (idle < inactivityTimeout) continue;

                    Kill(process);
                    await DrainAsync(pumps);
                    throw new UserFacingException(
                        $"{displayName} stopped responding and was closed.",
                        $"It printed nothing for {inactivityTimeout.TotalMinutes:N0} minutes. " +
                        $"The output so far is in {Path.GetFileName(logPath)}.");
                }
            }

            // Child processes (shader workers) can hold the pipes open after the tool itself exits.
            await DrainAsync(pumps);

            if (process.ExitCode != 0)
            {
                string lastLines;
                lock (logGate) lastLines = string.Join(Environment.NewLine, tail).Trim();
                throw new UserFacingException($"{displayName} failed (exit {process.ExitCode}).", lastLines);
            }
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await DrainAsync(pumps);
            throw;
        }
        finally
        {
            lock (_processLock)
                if (ReferenceEquals(_activeProcess, process)) _activeProcess = null;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception e) { Log.Warn($"Could not stop the conversion process: {e.Message}"); }
    }

    private static Task DrainAsync(Task pumps) => Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(10)));

    /// <summary>
    /// Checkpoints imported assets with the fingerprint of their intermediate source. A stale
    /// record authorizes replacement only when the user selected a replacement mode.
    /// </summary>
    internal static string BuildImportScript(string manifestPath, string resultPath, string ledgerPath, bool overwrite,
        bool replaceChanged = false)
    {
        static string PythonString(string value) => JsonSerializer.Serialize(value);
        return $$$"""
            import json
            import os
            import re
            import unreal

            manifest_path = {{{PythonString(manifestPath)}}}
            result_path = {{{PythonString(resultPath)}}}
            ledger_path = {{{PythonString(ledgerPath)}}}
            overwrite = {{{(overwrite ? "True" : "False")}}}
            replace_changed = {{{(replaceChanged ? "True" : "False")}}}

            # Characters Unreal replaces with '_' when it names an asset after its source file.
            INVALID_NAME = re.compile(r"[\"' ,/.:|&!~\n\r\t@#(){}\[\]=;^%$`]")

            def asset_path(item):
                name = INVALID_NAME.sub("_", os.path.splitext(os.path.basename(item["SourceFile"]))[0])
                return item["DestinationPath"] + "/" + name

            def load_ledger():
                if os.path.exists(ledger_path):
                    with open(ledger_path, "r", encoding="utf-8") as stream:
                        data = json.load(stream)
                    if "Version" not in data:
                        return {"Version": 2, "Entries": {}, "Legacy": data}
                    if data["Version"] != 2:
                        raise ValueError("Unsupported import-ledger version")
                    return data
                return {"Version": 2, "Entries": {}, "Legacy": {}}

            ledger = load_ledger()
            entries = ledger["Entries"]
            legacy = {os.path.normcase(path): paths for path, paths in ledger["Legacy"].items()}

            def asset_exists(path):
                return unreal.EditorAssetLibrary.does_asset_exist(path.split(".", 1)[0])

            def legacy_present(item, destination):
                paths = legacy.get(os.path.normcase(item["LegacySourceFile"]), [])
                if paths and all(path.casefold().startswith(destination + "/") for path in paths):
                    return all(asset_exists(path) for path in paths)
                return asset_exists(asset_path(item))

            def write_json(path, data):
                temporary = path + ".tmp"
                with open(temporary, "w", encoding="utf-8") as stream:
                    json.dump(data, stream, indent=2)
                os.replace(temporary, path)

            with open(manifest_path, "r", encoding="utf-8-sig") as stream:
                manifest = json.load(stream)

            present = 0
            stale_files = []
            pending = []
            for item in manifest:
                destination = item["DestinationPath"].casefold()
                record = entries.get(item["SourceKey"], {}).get(destination)
                replace = overwrite
                if not overwrite and record:
                    existing = [asset_exists(path) for path in record["Assets"]]
                    if record["Fingerprint"] == item["Fingerprint"]:
                        if existing and all(existing):
                            present += 1
                            continue
                    elif any(existing):
                        if not replace_changed:
                            stale_files.append(item["SourceFile"])
                            continue
                        replace = True
                elif not overwrite and legacy_present(item, destination):
                    present += 1
                    continue
                pending.append((item, destination, replace))

            failed_files = []
            imported = 0
            # Keep early checkpoints frequent without rewriting a growing ledger for every small batch.
            batch_size = 32
            offset = 0
            while offset < len(pending):
                batch = pending[offset:offset + batch_size]
                tasks = []
                for item, _, replace in batch:
                    task = unreal.AssetImportTask()
                    task.filename = item["SourceFile"]
                    task.destination_path = item["DestinationPath"]
                    task.automated = True
                    task.replace_existing = replace
                    task.replace_existing_settings = replace
                    task.save = True
                    tasks.append(task)
                unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)
                for task, (item, destination, _) in zip(tasks, batch):
                    paths = [str(path) for path in task.imported_object_paths]
                    if paths:
                        imported += 1
                        entries.setdefault(item["SourceKey"], {})[destination] = {
                            "Fingerprint": item["Fingerprint"], "Assets": paths,
                        }
                    else:
                        failed_files.append(task.filename)
                write_json(ledger_path, ledger)
                offset += len(batch)
                batch_size *= 2

            result = {"Imported": imported, "Failed": len(failed_files), "FailedFiles": failed_files,
                      "AlreadyPresent": present, "StaleFiles": stale_files}
            write_json(result_path, result)
            unreal.log("UEBulkExport dump conversion: imported={}, failed={}, already present={}, stale={}".format(
                imported, len(failed_files), present, len(stale_files)))
            """;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed record ImportManifestEntry(string SourceFile, string DestinationPath, string SourceKey,
        string LegacySourceFile, string Fingerprint);
    private sealed record ImportResult(int Imported, int Failed, string[]? FailedFiles = null, int AlreadyPresent = 0,
        string[]? StaleFiles = null);

    /// <summary>
    /// A temporary view for UE Viewer to read, minus the packages it must skip. Writable files
    /// use hard links when possible. Read-only files are copied so cleanup can preserve the
    /// original attributes.
    /// </summary>
    internal sealed class SourceMirror : IDisposable
    {
        private readonly string _source;
        private bool _ownsRoot;

        public string Root { get; }

        /// <summary>True when at least one file had to be copied because hard links were impossible.</summary>
        public bool UsedCopies { get; private set; }

        private SourceMirror(string source, string root)
        {
            _source = source;
            Root = root;
        }

        /// <summary>
        /// Prefers the working directory, then a hidden folder beside the dump (same volume, so hard
        /// links work), and falls back to copying into the working directory.
        /// </summary>
        public static SourceMirror Create(string source, string workingDirectory,
            IReadOnlySet<string> excluded, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var name = $".uebulkexport-mirror-{Guid.NewGuid():N}";
            var inWorking = Path.Combine(workingDirectory, name);
            var parent = Path.GetDirectoryName(source);
            var linkRoot = SameVolume(source, workingDirectory) ? inWorking
                : parent is not null ? Path.Combine(parent, name) : null;

            if (linkRoot is not null)
            {
                var mirror = new SourceMirror(source, linkRoot);
                try
                {
                    mirror.Populate(excluded, allowCopies: false, ct);
                    return mirror;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    mirror.Dispose();
                }
                catch
                {
                    mirror.Dispose();
                    throw;
                }
            }

            var copied = new SourceMirror(source, inWorking);
            try
            {
                copied.Populate(excluded, allowCopies: true, ct);
                return copied;
            }
            catch
            {
                copied.Dispose();
                throw;
            }
        }

        public void Exclude(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(Root, relative));
            if (_ownsRoot && IsInsideOrSame(path, Root) && File.Exists(path)) File.Delete(path);
        }

        public void Dispose()
        {
            if (!_ownsRoot) return;
            try
            {
                Directory.Delete(Root, recursive: true);
                _ownsRoot = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not remove the temporary conversion mirror '{Root}': {e.Message}");
            }
        }

        private void Populate(IReadOnlySet<string> excluded, bool allowCopies, CancellationToken ct)
        {
            if (IsInsideOrSame(Root, _source) || Directory.Exists(Root))
                throw new IOException($"The mirror folder cannot be used: {Root}");
            Directory.CreateDirectory(Root);
            _ownsRoot = true;

            foreach (var file in EnumerateDumpFiles(_source))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(_source, file);
                if (excluded.Contains(relative) ||
                    relative.EndsWith(".uebskip", StringComparison.OrdinalIgnoreCase)) continue;

                var target = Path.Combine(Root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // A read-only hard link cannot be removed without changing the original's attributes.
                var readOnly = File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly);
                if (!readOnly && CreateHardLink(target, file, IntPtr.Zero)) continue;

                if (!readOnly && !allowCopies)
                    throw new IOException($"Cannot create a hard link to {file} (error {Marshal.GetLastWin32Error()}).");
                File.Copy(file, target);
                if (readOnly) File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                UsedCopies = true;
            }
        }

        private static bool SameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    }
}

public sealed record DumpConversionOptions(
    string SourceDirectory,
    string SourceVersion,
    string UModelPath,
    string TargetProject,
    string UnrealEditorPath,
    string DestinationPath,
    string? WorkingDirectory = null,
    bool Overwrite = false,
    TimeSpan? UModelInactivityTimeout = null,
    TimeSpan? EditorInactivityTimeout = null,
    string? MappingsPath = null,
    string? VgmStreamPath = null,
    bool ReplaceChanged = false);

public sealed record DumpConversionProgress(string Stage, double Fraction, string Message);

public enum DumpConversionDiagnosticLevel { Warning, Error }

/// <summary>Semantic read-only check, localized by the desktop presentation layer.</summary>
public sealed record DumpConversionDiagnostic(DumpConversionDiagnosticLevel Level, string Code, string? Detail = null);

public sealed record DumpConversionPreflight(
    int CookedPackages,
    int MapPackages,
    int PayloadFiles,
    int EmptyPayloadFiles,
    int ImportableFiles,
    int ActorXFiles,
    int HiddenPackages,
    long SourceBytes,
    string TargetVersion,
    string DestinationPath,
    string WorkingDirectory,
    IReadOnlyList<DumpConversionDiagnostic> Diagnostics)
{
    public bool CanProceed => Diagnostics.All(item => item.Level != DumpConversionDiagnosticLevel.Error);
}

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
    TimeSpan Elapsed,
    int AlreadyPresent = 0,
    int StaleFiles = 0);
