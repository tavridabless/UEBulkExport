using CUE4Parse.FileProvider.Objects;

namespace UEBulkExport.Gui.Services;

/// <summary>The outcome of a scan: what was mounted and every readable entry, for the Browser tab.</summary>
public sealed record ScanResult(
    string PaksDirectory,
    IReadOnlyList<ContainerInfo> Containers,
    IReadOnlyList<GameFile> Files);

/// <summary>
/// Runs the exporter off the UI thread. One operation at a time; progress comes back through
/// events raised on whatever thread produced them - the view model marshals to the UI.
/// </summary>
public sealed class ExportService
{
    private CancellationTokenSource? _cancellation;

    public bool IsBusy => _cancellation is not null;

    public event Action<ExportProgress>? ProgressChanged;

    /// <summary>Mounts the containers and lists their entries. Does not write anything.</summary>
    public Task<ScanResult> ScanAsync(Options options) => Run(ct => Task.Run(() =>
    {
        var scanOptions = Clone(options);
        scanOptions.Mode = ExportMode.List;
        scanOptions.IncludeRegex = null;
        scanOptions.ExcludeRegex = null;
        scanOptions.PathsFile = null;
        scanOptions.SelectedPaths = null;
        Cli.Prepare(scanOptions);

        Natives.Initialize(scanOptions);

        using var exporter = new BulkExporter(scanOptions);
        exporter.Mount();
        ct.ThrowIfCancellationRequested();

        var files = exporter.AllFiles
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScanResult(scanOptions.PaksDirectory, exporter.Containers, files);
    }, ct));

    /// <summary>Works out what a run would do, without writing.</summary>
    public Task<ExportPlan> DryRunAsync(Options options) => Run(ct => Task.Run(() =>
    {
        var o = Clone(options);
        o.DryRun = true;
        Cli.Prepare(o);

        Natives.Initialize(o);

        using var exporter = new BulkExporter(o);
        exporter.Mount();
        var files = exporter.SelectFiles();
        exporter.VerifyMappings(files);
        ct.ThrowIfCancellationRequested();

        var plan = exporter.BuildPlan(files);
        exporter.PrintPlan(files);
        return plan;
    }, ct));

    public Task<ExportSummary> ExportAsync(Options options) => Run(async ct =>
    {
        var o = Clone(options);
        o.DryRun = false;
        Cli.Prepare(o);

        Directory.CreateDirectory(o.OutputDirectory);
        Log.OpenFile(Path.Combine(o.OutputDirectory, "UEBulkExport.log"));

        try
        {
            Log.Info($"UEBulkExport {Cli.Version} - {o.Mode} export");
            Log.Info($"containers: {o.PaksDirectory}");

            return await Task.Run(async () =>
            {
                Natives.Initialize(o);

                using var exporter = new BulkExporter(o);
                exporter.ProgressChanged += p => ProgressChanged?.Invoke(p);
                exporter.Mount();

                var files = exporter.SelectFiles();
                exporter.VerifyMappings(files);
                ct.ThrowIfCancellationRequested();

                return await exporter.RunAsync(files, ct);
            }, ct);
        }
        finally
        {
            Log.Close();
        }
    });

    public void Cancel() => _cancellation?.Cancel();

    private async Task<T> Run<T>(Func<CancellationToken, Task<T>> work)
    {
        if (_cancellation is not null)
            throw new InvalidOperationException("an operation is already running");

        _cancellation = new CancellationTokenSource();
        try
        {
            return await work(_cancellation.Token);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Prepare() rewrites paths in place; work on a copy so the form keeps what the user typed.</summary>
    private static Options Clone(Options o) => new()
    {
        PaksDirectory = o.PaksDirectory,
        OutputDirectory = o.OutputDirectory,
        UsmapPath = o.UsmapPath,
        OodlePath = o.OodlePath,
        ZlibPath = o.ZlibPath,
        VgmStreamPath = o.VgmStreamPath,
        RetocPath = o.RetocPath,
        Game = o.Game,
        Platform = o.Platform,
        Mode = o.Mode,
        AesKeys = [.. o.AesKeys],
        Threads = o.Threads,
        IncludeRegex = o.IncludeRegex,
        ExcludeRegex = o.ExcludeRegex,
        PathsFile = o.PathsFile,
        SelectedPaths = o.SelectedPaths,
        WriteJson = o.WriteJson,
        WriteAssets = o.WriteAssets,
        WriteRawMisc = o.WriteRawMisc,
        WriteRawPackages = o.WriteRawPackages,
        ExportMaterials = o.ExportMaterials,
        ConvertAudio = o.ConvertAudio,
        ExportWorlds = o.ExportWorlds,
        Resume = o.Resume,
        Verbose = o.Verbose,
        DryRun = o.DryRun,
        MeshFormat = o.MeshFormat,
        AnimFormatOverride = o.AnimFormatOverride,
        NaniteMeshFormat = o.NaniteMeshFormat,
        MeshQuality = o.MeshQuality,
        TextureFormat = o.TextureFormat,
        SocketFormat = o.SocketFormat,
        ExportMorphTargets = o.ExportMorphTargets,
        ExportAllTextureMips = o.ExportAllTextureMips
    };
}
