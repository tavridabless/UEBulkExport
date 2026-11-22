using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UEBulkExport;

public sealed record BlueprintProjectRecoveryResult(string OutputDirectory, string? ProjectPath,
    BlueprintBatchReconstructionResult BlueprintCoverage, bool HasGaps);

public static class BlueprintProjectRecovery
{
    private const string BlueprintDestination = "/Game/RecoveredBlueprints";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Task<BlueprintProjectRecoveryResult> RecoverAsync(BlueprintRecoveryReport report, string editor,
        string output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return RecoverAsync(report, null, null, editor, output, null, false, cancellationToken, null);
    }

    public static Task<BlueprintProjectRecoveryResult> RecoverAsync(BlueprintRecoveryReport report, Options source,
        string editor, string output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(source);
        return RecoverAsync(report, source, null, editor, output, null, false, cancellationToken, null);
    }

    internal static Task<BlueprintProjectRecoveryResult> RecoverAsync(Options source, BlueprintRecoveryLimits limits,
        string editor, string output, string? managedDirectory, bool decompile, CancellationToken ct, string? meshMap) =>
        RecoverAsync(null, source, limits, editor, output, managedDirectory, decompile, ct, meshMap);

    private static async Task<BlueprintProjectRecoveryResult> RecoverAsync(BlueprintRecoveryReport? snapshot,
        Options? source, BlueprintRecoveryLimits? limits, string editor, string output, string? managedDirectory,
        bool decompile, CancellationToken ct, string? meshMap)
    {
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("Blueprint dump requires Unreal Editor 5.8 on Windows.");
        if (!File.Exists(editor))
            throw new UserFacingException("The Unreal Editor executable is missing.");
        if (managedDirectory is not null && !Directory.Exists(managedDirectory))
            throw new UserFacingException("The managed assembly directory is missing.");
        if (meshMap is not null && !File.Exists(meshMap))
            throw new UserFacingException("The Blueprint mesh map is missing.");
        ct.ThrowIfCancellationRequested();

        var workspace = Path.Combine(Path.GetFullPath(output), "recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Log.Raw($"Recovery output: {workspace}");
        var resultPath = Path.Combine(workspace, "recovery-result.json");
        var reportPath = Path.Combine(workspace, "blueprint-report.json");
        var mapPath = Path.Combine(workspace, "blueprint-dependencies.json");
        var planPath = Path.Combine(workspace, "blueprint-plan.json");
        var meshImports = Path.Combine(workspace, "mesh-imports.json");
        BlueprintBatchReconstructionResult? coverage = null;
        ManagedDump? managed = null;
        var meshWarnings = 0;
        var editorMeshWarnings = 0;
        string? project = null, importReport = null;
        var status = "Failed";
        try
        {
            if (limits is not null) BlueprintRecovery.ValidateLimits(limits);
            using (var archive = snapshot is null ? BlueprintRecovery.OpenSource(source!, ct) : null)
            {
                var report = snapshot ?? BlueprintRecovery.ReadReport(archive!, limits!, ct);
                BlueprintRecovery.WriteReport(report, reportPath);
                BlueprintRecovery.WriteDependencyMap(report, mapPath);
                var plan = BlueprintRecovery.WriteBatchPlan(report, planPath, ct);
                coverage = plan.Coverage!.Summary;
                if (source is not null)
                    meshWarnings = await PrepareMeshesAsync(plan, source, archive, workspace, meshImports, meshMap, ct);
            }
            Log.Raw($"Blueprints in plan: {coverage.ClassesIncluded}/{coverage.ClassesInspected}. " +
                    $"Functions reconstructed: {coverage.FunctionsReconstructed}; stubs: {coverage.FunctionsStubbed}. " +
                    $"Events reconstructed: {coverage.EventsReconstructed}; components: {coverage.ComponentsReconstructed}; " +
                    $"component overrides: {coverage.ComponentOverridesReconstructed}.");

            if (managedDirectory is not null)
            {
                managed = RecoverManaged(Path.GetFullPath(managedDirectory), workspace, decompile, ct);
                Log.Raw($"Verified original C# documents: {managed.VerifiedDocuments} in {managed.Assemblies.Count} managed or unreadable inputs.");
                if (decompile) Log.Raw($"Decompiled C# projects: {managed.DecompiledProjects}.");
            }
            if (coverage.ClassesIncluded > 0)
            {
                (project, importReport) = await RestoreProjectAsync(workspace, planPath, editor, meshMap,
                    File.Exists(meshImports) ? meshImports : null, ct);
                if (File.Exists(meshImports))
                {
                    using var file = File.OpenRead(importReport);
                    using var nativeReport = await JsonDocument.ParseAsync(file, cancellationToken: ct);
                    if (nativeReport.RootElement.TryGetProperty("meshWarnings", out var warnings))
                        editorMeshWarnings = warnings.GetArrayLength();
                }
                Log.Raw($"Blueprint reconstruction report: {importReport}");
            }
            var partial = coverage.HasGaps || managed is { HasGaps: true } || meshWarnings > 0 || editorMeshWarnings > 0;
            status = partial ? "Partial" : "Restored";
            if (partial) Log.Raw("Recovery is partial. See recovery-result.json for the coverage and diagnostics reports.");
            return new BlueprintProjectRecoveryResult(workspace, project, coverage, partial);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "Cancelled";
            throw;
        }
        finally
        {
            AtomicFile.WriteAllBytes(resultPath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                status,
                project = ExistingRelativePath(project),
                blueprintReport = ExistingRelativePath(reportPath),
                dependencyMap = ExistingRelativePath(mapPath),
                blueprintPlan = ExistingRelativePath(planPath),
                blueprintImportReport = ExistingRelativePath(importReport),
                meshImports = ExistingRelativePath(meshImports),
                meshWarnings,
                editorMeshWarnings,
                blueprintCoverage = coverage,
                managedSource = managed
            }, Json));
        }

        string? ExistingRelativePath(string? path) => File.Exists(path) ? Path.GetRelativePath(workspace, path!) : null;
    }

    private static async Task<int> PrepareMeshesAsync(BlueprintLinkedReconstructionPlan plan, Options source,
        BulkExporter? mounted, string workspace, string manifestPath, string? meshMap, CancellationToken ct)
    {
        var referenced = plan.Blueprints.SelectMany(blueprint =>
                (blueprint.Components ?? []).Select(component => component.StaticMesh)
                .Concat((blueprint.ComponentOverrides ?? []).Select(component => component.StaticMesh)))
            .OfType<string>().Where(mesh => mesh.StartsWith("/Game/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (referenced.Length == 0) return 0;
        if (meshMap is not null)
        {
            var mapped = ReadMappedMeshes(meshMap);
            referenced = referenced.Where(mesh => !mapped.Contains(mesh)).ToArray();
        }
        if (referenced.Length == 0) return 0;
        using var separate = mounted is null ? BlueprintRecovery.OpenSource(source, ct) : null;
        separate?.Mount();
        var transfer = await (mounted ?? separate!).ExtractBlueprintMeshesAsync(referenced,
            Path.Combine(workspace, "MeshGeometry"), ct);
        var meshes = transfer.Meshes.Select(mesh => mesh with
        {
            GeometryPath = RelativeAssetPath(mesh.GeometryPath),
            Lods = mesh.Lods.Select(lod => lod with { GeometryPath = RelativeAssetPath(lod.GeometryPath) }).ToArray()
        }).ToArray();
        var textures = transfer.Textures.Select(texture => texture with { FilePath = RelativeAssetPath(texture.FilePath) }).ToArray();
        AtomicFile.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            meshes,
            transfer.Materials,
            textures,
            issues = transfer.Issues,
            warnings = transfer.Warnings
        }, Json));
        Log.Raw($"Static meshes prepared: {meshes.Length}/{referenced.Length}. " +
                $"Materials: {transfer.Materials.Count}; textures: {textures.Length}; transfer warnings: {transfer.Warnings.Count}.");
        if (transfer.Issues.Count > 0)
            throw new UserFacingException($"Could not prepare {transfer.Issues.Count} referenced static meshes.",
                $"See {manifestPath} or use --mesh-map for an available editor asset.");
        return transfer.Warnings.Count;

        string RelativeAssetPath(string path) => Path.GetRelativePath(workspace, path).Replace('\\', '/');
    }

    private static HashSet<string> ReadMappedMeshes(string meshMap)
    {
        try
        {
            using var file = File.OpenRead(meshMap);
            using var document = JsonDocument.Parse(file);
            var root = document.RootElement;
            if (!root.TryGetProperty("meshes", out var meshes) || meshes.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            return meshes.EnumerateObject().Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            throw new UserFacingException("The Blueprint mesh map could not be read.", "Use a JSON file with a meshes object.");
        }
    }

    private static async Task<(string Project, string Report)> RestoreProjectAsync(string workspace, string plan,
        string editor, string? meshMap, string? meshImports, CancellationToken ct)
    {
        var destination = Path.Combine(workspace, "Project");
        // UE 5.8 Interchange requires 60 characters of headroom in the legacy Windows
        // 260-character path budget before it can import, even for short asset names.
        var useStaging = meshImports is not null && Path.Combine(destination, "Content").Length + 1 > 200;
        if (!useStaging)
        {
            var project = CreateEditorProject(destination);
            var report = await BlueprintEditorRestore.RestoreAsync(plan, editor, project, BlueprintDestination,
                ct, meshMap, meshImports);
            return (project, report);
        }

        var staging = Directory.CreateTempSubdirectory("UEBulkMesh-").FullName;
        var removeStaging = true;
        try
        {
            var project = CreateEditorProject(staging);
            var report = await BlueprintEditorRestore.RestoreAsync(plan, editor, project, BlueprintDestination,
                ct, meshMap, meshImports);
            ct.ThrowIfCancellationRequested();
            var relativeReport = Path.GetRelativePath(staging, report);
            PublishDirectory(staging, destination, ct);
            return (Path.Combine(destination, Path.GetFileName(project)), Path.Combine(destination, relativeReport));
        }
        catch (UserFacingException error) when (Directory.Exists(Path.Combine(staging, "Saved", "UEBulkExport")))
        {
            var diagnostics = Path.Combine(workspace, "EditorDiagnostics");
            try { PublishDirectory(Path.Combine(staging, "Saved", "UEBulkExport"), diagnostics, CancellationToken.None); }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Temporary editor logs were left in {staging}: {copyError.Message}");
                removeStaging = false;
            }
            if (!removeStaging) throw;
            throw new UserFacingException(error.Headline, $"See the Unreal Editor logs in {diagnostics}.");
        }
        finally
        {
            if (removeStaging) CleanStaging(staging);
        }
    }

    private static string CreateEditorProject(string directory)
    {
        var config = Path.Combine(directory, "Config");
        Directory.CreateDirectory(config);
        var project = Path.Combine(directory, "RecoveredProject.uproject");
        File.WriteAllText(project, """
            {
              "FileVersion": 3,
              "EngineAssociation": "5.8",
              "Plugins": [
                { "Name": "PythonScriptPlugin", "Enabled": true }
              ]
            }
            """);
        // The generated project uses the installed disk cache even when local Zen is unavailable.
        File.WriteAllText(Path.Combine(config, "DefaultEngine.ini"), """
            [DerivedDataCacheGraphs]
            Installed=(InstalledProjectPak,EnginePak=InstalledEnginePak,Local=InstalledLocal)
            """);
        return project;
    }

    private static void PublishDirectory(string source, string destination, CancellationToken ct)
    {
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, destination);
            return;
        }

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        Directory.CreateDirectory(temporary);
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var path in Directory.EnumerateFiles(source, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                var output = Path.Combine(temporary, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(path, output);
            }
            ct.ThrowIfCancellationRequested();
            Directory.Move(temporary, destination);
        }
        finally { CleanStaging(temporary); }
    }

    private static void CleanStaging(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not remove recovery staging directory {path}: {error.Message}");
        }
    }

    private static ManagedDump RecoverManaged(string input, string workspace, bool decompile, CancellationToken ct)
    {
        var assemblies = new List<ManagedDumpAssembly>();
        var nativeBinaries = 0;
        var folders = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false };
        var sourceOutput = Path.Combine(workspace, "CSharp");
        var candidates = Directory.EnumerateFiles(input, "*", folders).Where(IsExecutableOrLibrary)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var referenceDirectories = decompile
            ? candidates.Select(path => Path.GetDirectoryName(path)!).Prepend(input)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(input, path);
            try
            {
                using (var pe = new PEReader(File.OpenRead(path)))
                {
                    if (!pe.HasMetadata)
                    {
                        nativeBinaries++;
                        continue;
                    }
                }
                assemblies.Add(RecoverAssembly(path, relative, sourceOutput, referenceDirectories, workspace, decompile, ct));
            }
            catch (Exception error) when (error is UserFacingException or IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                assemblies.Add(new ManagedDumpAssembly(relative, null, error.Message));
            }
        }
        return new ManagedDump(assemblies, nativeBinaries);

        bool IsExecutableOrLibrary(string path)
        {
            ct.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path);
            return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ManagedDumpAssembly RecoverAssembly(string path, string relative, string sourceOutput,
        IReadOnlyList<string> referenceDirectories, string workspace, bool decompile, CancellationToken ct)
    {
        ManagedSourceReport? report = null;
        string? originalError = null;
        try
        {
            report = ManagedSourceRecovery.Run(path, outputParent: sourceOutput, ct: ct);
            if (report.OutputDirectory is { } directory)
                report = report with { OutputDirectory = Path.GetRelativePath(workspace, directory) };
        }
        catch (UserFacingException error)
        {
            originalError = error.Message;
        }
        if (!decompile || report is { Verified: > 0 })
            return new ManagedDumpAssembly(relative, report, originalError);

        try
        {
            var restored = ManagedSourceDecompilation.Run(path, sourceOutput, referenceDirectories, ct);
            return new ManagedDumpAssembly(relative, report, originalError, restored with
            {
                OutputDirectory = Path.GetRelativePath(workspace, restored.OutputDirectory),
                ProjectPath = Path.GetRelativePath(workspace, restored.ProjectPath)
            });
        }
        catch (UserFacingException error)
        {
            return new ManagedDumpAssembly(relative, report, originalError, DecompilationError: error.Message);
        }
    }

    private sealed record ManagedDumpAssembly(string Path, ManagedSourceReport? Report, string? Error,
        ManagedSourceDecompilationReport? Decompiled = null, string? DecompilationError = null)
    {
        public bool HasGaps => Decompiled is { } fallback ? fallback.HasGaps :
            Report is not { Verified: > 0 } source || source.DocumentLimitReached || source.Documents.Count != source.Verified;
    }

    private sealed record ManagedDump(IReadOnlyList<ManagedDumpAssembly> Assemblies, int NativeBinariesSkipped)
    {
        public int VerifiedDocuments => Assemblies.Sum(item => item.Report?.Verified ?? 0);
        public int DecompiledProjects => Assemblies.Count(item => item.Decompiled is not null);
        public bool HasGaps => Assemblies.Count == 0 || Assemblies.Any(item => item.HasGaps);
    }
}
