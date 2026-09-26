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
/// UE Viewer and asking the destination Unreal Editor to import those files. This deliberately
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

    /// <summary>UE Viewer prints a line per package; this long without one means it is stuck.</summary>
    public static readonly TimeSpan DefaultUModelInactivityTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The editor logs continuously while it imports; half an hour of silence means it hangs on a
    /// dialog, a crash reporter or a stuck importer rather than doing work.
    /// </summary>
    public static readonly TimeSpan DefaultEditorInactivityTimeout = TimeSpan.FromMinutes(30);

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
        var source = Path.GetFullPath(options.SourceDirectory.Trim()).TrimEnd(Path.DirectorySeparatorChar);
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

        // Builds before this one renamed packages inside the dump; put back anything a crash left behind.
        await Task.Run(() => RecoverHiddenPackages(source), ct);
        var cookedPackages = await Task.Run(() =>
            Directory.EnumerateFiles(source, "*.uasset", SearchOption.AllDirectories).Count(), ct);
        var importRoot = source;

        if (cookedPackages > 0)
        {
            progress?.Report(new("Exporting", 0.15,
                $"UE Viewer is exporting {cookedPackages:N0} cooked packages."));

            var umodelLog = Path.Combine(workingDirectory, "umodel.log");
            if (File.Exists(umodelLog)) File.Delete(umodelLog);

            // Relative paths of packages UE Viewer must not see.
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var corrupt in await Task.Run(() => FindPackagesWithEmptyPayloads(source).ToList(), ct))
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

        WriteFailureReport(errorReportPath, failures);

        progress?.Report(new("PreparingImport", 0.55, "Preparing files for the target Unreal Editor."));
        var files = await Task.Run(() => EnumerateImportableFiles(importRoot).ToArray(), ct);
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
        var ledgerPath = Path.Combine(workingDirectory, "import-ledger.json");
        var manifest = files.Select(path => new ImportManifestEntry(
            path,
            BuildAssetDestination(importRoot, path, destination))).ToArray();

        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8, ct);
        await File.WriteAllTextAsync(scriptPath,
            BuildImportScript(manifestPath, resultPath, ledgerPath, options.Overwrite), Encoding.UTF8, ct);
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
            started.Elapsed,
            result.AlreadyPresent);
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

    internal static string DefaultWorkingDirectory(string projectDirectory, string source, string sourceVersion)
    {
        var sourceName = SanitizePathComponent(new DirectoryInfo(source).Name);
        var version = SanitizePathComponent(sourceVersion);

        // Two dumps may share a folder name; their exports must not mix.
        var identity = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
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

    /// <summary>Undoes the in-place renames earlier 2.1.0 builds used, if a crash left any behind.</summary>
    private static void RecoverHiddenPackages(string root)
    {
        foreach (var hidden in Directory.EnumerateFiles(root, "*.uasset.uebskip", SearchOption.AllDirectories))
        {
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
        await using var log = new StreamWriter(logPath, appendLog, new UTF8Encoding(false)) { AutoFlush = true };
        if (appendLog) await log.WriteLineAsync($"----- attempt {DateTime.Now:O} -----");

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
    }

    private static Task DrainAsync(Task pumps) => Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(10)));

    /// <summary>
    /// The script the target editor runs. Every successful import is written to a ledger in the
    /// working directory. Without Overwrite, a file whose recorded assets all still exist (or, for
    /// files imported before the ledger existed, an asset of the same name in its destination
    /// folder) is left alone and reported as already present, so a second run or a resume neither
    /// re-imports it nor lists it as a failure.
    /// </summary>
    internal static string BuildImportScript(string manifestPath, string resultPath, string ledgerPath, bool overwrite)
    {
        static string PythonString(string value) => JsonSerializer.Serialize(value);
        return $$"""
            import json
            import os
            import re
            import unreal

            manifest_path = {{PythonString(manifestPath)}}
            result_path = {{PythonString(resultPath)}}
            ledger_path = {{PythonString(ledgerPath)}}
            overwrite = {{(overwrite ? "True" : "False")}}

            # Characters Unreal replaces with '_' when it names an asset after its source file.
            INVALID_NAME = re.compile(r"[\"' ,/.:|&!~\n\r\t@#(){}\[\]=;^%$`]")

            def asset_path(item):
                name = INVALID_NAME.sub("_", os.path.splitext(os.path.basename(item["SourceFile"]))[0])
                return item["DestinationPath"] + "/" + name

            def load_ledger():
                if os.path.exists(ledger_path):
                    with open(ledger_path, "r", encoding="utf-8") as stream:
                        return json.load(stream)
                return {}

            ledger = load_ledger()

            def already_present(item):
                recorded = ledger.get(item["SourceFile"])
                if recorded:
                    return all(unreal.EditorAssetLibrary.does_asset_exist(path.split(".", 1)[0]) for path in recorded)
                return unreal.EditorAssetLibrary.does_asset_exist(asset_path(item))

            with open(manifest_path, "r", encoding="utf-8-sig") as stream:
                manifest = json.load(stream)

            present = []
            tasks = []
            for item in manifest:
                if not overwrite and already_present(item):
                    present.append(item["SourceFile"])
                    continue
                task = unreal.AssetImportTask()
                task.filename = item["SourceFile"]
                task.destination_path = item["DestinationPath"]
                task.automated = True
                task.replace_existing = overwrite
                task.replace_existing_settings = overwrite
                task.save = True
                tasks.append(task)

            if tasks:
                unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)

            failed_files = []
            imported = 0
            for task in tasks:
                paths = [str(path) for path in task.imported_object_paths]
                if paths:
                    imported += 1
                    ledger[task.filename] = paths
                else:
                    failed_files.append(task.filename)

            with open(ledger_path, "w", encoding="utf-8") as stream:
                json.dump(ledger, stream, indent=2)
            result = {"Imported": imported, "Failed": len(failed_files), "FailedFiles": failed_files,
                      "AlreadyPresent": len(present)}
            with open(result_path, "w", encoding="utf-8") as stream:
                json.dump(result, stream, indent=2)
            unreal.log("UEBulkExport dump conversion: imported={}, failed={}, already present={}".format(
                imported, len(failed_files), len(present)))
            """;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed record ImportManifestEntry(string SourceFile, string DestinationPath);
    private sealed record ImportResult(int Imported, int Failed, string[]? FailedFiles = null, int AlreadyPresent = 0);

    /// <summary>
    /// A read-only view of the dump for UE Viewer, minus the packages it must skip. Files are hard
    /// links where the file system allows it, so the copy costs no space and no time; the dump
    /// itself is never renamed, moved or written.
    /// </summary>
    internal sealed class SourceMirror : IDisposable
    {
        private readonly string _source;

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
            var inWorking = Path.Combine(workingDirectory, "Source");
            var candidates = new List<string>();
            if (SameVolume(source, workingDirectory)) candidates.Add(inWorking);

            var parent = Path.GetDirectoryName(source);
            if (!string.IsNullOrEmpty(parent))
                candidates.Add(Path.Combine(parent, $".{Path.GetFileName(source)}.uebulkexport-mirror"));

            foreach (var root in candidates)
            {
                var mirror = new SourceMirror(source, root);
                try
                {
                    mirror.Populate(excluded, allowCopies: false, ct);
                    return mirror;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    mirror.Dispose();
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
            var path = Path.Combine(Root, relative);
            if (File.Exists(path)) DeleteLink(path);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    DeleteLink(file);
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover working copy is harmless and is replaced on the next run.
            }
        }

        private void Populate(IReadOnlySet<string> excluded, bool allowCopies, CancellationToken ct)
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            Directory.CreateDirectory(Root);

            foreach (var file in Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(_source, file);
                if (excluded.Contains(relative) ||
                    relative.EndsWith(".uebskip", StringComparison.OrdinalIgnoreCase)) continue;

                var target = Path.Combine(Root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (CreateHardLink(target, file, IntPtr.Zero)) continue;

                if (!allowCopies)
                    throw new IOException($"Cannot create a hard link to {file} (error {Marshal.GetLastWin32Error()}).");
                File.Copy(file, target);
                UsedCopies = true;
            }
        }

        /// <summary>
        /// A hard link shares its attributes with the original, so clearing read-only to delete the
        /// link would change the dump; put the attribute back on the original afterwards.
        /// </summary>
        private void DeleteLink(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == 0)
            {
                File.Delete(path);
                return;
            }

            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
            var original = Path.Combine(_source, Path.GetRelativePath(Root, path));
            if (File.Exists(original) && (File.GetAttributes(original) & FileAttributes.ReadOnly) == 0)
                File.SetAttributes(original, File.GetAttributes(original) | FileAttributes.ReadOnly);
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
    TimeSpan? EditorInactivityTimeout = null);

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
    TimeSpan Elapsed,
    int AlreadyPresent = 0);
