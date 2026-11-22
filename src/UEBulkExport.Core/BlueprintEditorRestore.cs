using System.Diagnostics;
using System.Text.Json;

namespace UEBulkExport;

internal static class BlueprintEditorRestore
{
    public static async Task<string> RestoreAsync(string plan, string editor, string project, string destination,
        CancellationToken ct, string? meshMap = null, string? meshImports = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new UserFacingException("Blueprint restoration through Unreal Editor currently supports Windows.");
        ct.ThrowIfCancellationRequested();
        plan = ExistingPath(plan, "Blueprint reconstruction plan");
        editor = ExistingPath(editor, "Unreal Editor executable");
        project = ExistingPath(project, "Target .uproject");
        if (meshMap is not null) meshMap = ExistingPath(meshMap, "Blueprint mesh map");
        if (meshImports is not null) meshImports = ExistingPath(meshImports, "Blueprint mesh import manifest");
        if (new FileInfo(plan).Length > 16 * 1024 * 1024)
            throw new UserFacingException("The Blueprint reconstruction plan exceeds 16 MiB.");
        if (!Path.GetExtension(project).Equals(".uproject", StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("Choose a target .uproject file.");

        using var adapter = typeof(BlueprintEditorRestore).Assembly.GetManifestResourceStream("UEBulkExport.ImportBlueprint")
            ?? throw new UserFacingException("This build is missing the Blueprint import adapter.");
        var projectDirectory = Path.GetDirectoryName(project)!;
        var workspace = Path.Combine(projectDirectory, "Saved", "UEBulkExport", "BlueprintRecovery",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var script = Path.Combine(workspace, "import_blueprint.py");
        await using (var file = File.Create(script))
            await adapter.CopyToAsync(file, ct);
        var report = Path.Combine(workspace, "import-report.json");
        Log.Info($"Blueprint reconstruction log directory: {workspace}");

        var start = new ProcessStartInfo(editor)
        {
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-run=pythonscript");
        start.ArgumentList.Add("-script=" + script.Replace('\\', '/'));
        foreach (var flag in new[] { "-unattended", "-nop4", "-nosplash", "-NullRHI" })
            start.ArgumentList.Add(flag);
        start.ArgumentList.Add("-abslog=" + Path.Combine(workspace, "unreal.log"));
        start.Environment["UEBULKEXPORT_BLUEPRINT_PLAN"] = plan;
        start.Environment["UEBULKEXPORT_BLUEPRINT_DESTINATION"] = destination;
        start.Environment["UEBULKEXPORT_BLUEPRINT_REPORT"] = report;
        start.Environment["UEBULKEXPORT_BLUEPRINT_MESH_MAP"] = meshMap ?? "";
        start.Environment["UEBULKEXPORT_BLUEPRINT_MESH_IMPORTS"] = meshImports ?? "";

        await using var stdoutFile = File.Create(Path.Combine(workspace, "stdout.log"));
        await using var stderrFile = File.Create(Path.Combine(workspace, "stderr.log"));
        using var process = Process.Start(start) ?? throw new UserFacingException("Unreal Editor could not be started.");
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(stdoutFile);
        var stderr = process.StandardError.BaseStream.CopyToAsync(stderrFile);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally { await Task.WhenAll(stdout, stderr); }

        if (process.ExitCode != 0 || !File.Exists(report))
            throw new UserFacingException($"Unreal Editor did not complete Blueprint reconstruction (exit {process.ExitCode}).",
                $"See {workspace}. Check the Python Editor Script Plugin and required assets in the target UE 5.8 project.");
        using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(report, ct));
        var import = result.RootElement;
        var assets = import.TryGetProperty("createdBlueprints", out var group)
            ? group.EnumerateArray().Select(entry => entry.GetProperty("createdBlueprint").GetString()).ToArray()
            : [import.GetProperty("createdBlueprint").GetString()];
        foreach (var asset in assets) Log.Raw($"Created Blueprint: {asset}");
        return report;
    }

    private static string ExistingPath(string path, string description) =>
        File.Exists(path) ? Path.GetFullPath(path) : throw new UserFacingException($"{description} is missing.");
}
