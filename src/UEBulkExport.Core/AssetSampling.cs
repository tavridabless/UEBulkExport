using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;

namespace UEBulkExport;

public enum AssetSampleType { Texture, Mesh, Animation, Audio, Level, Material }
public enum AssetSampleStatus { Converted, NoOutput, Failed }
public enum AssetSampleCoverageStatus { Complete, Missing, Incomplete, PackageLimitReached, Cancelled }

public sealed record AssetSampleRequest(AssetSampleType? Type = null, int PerType = 1, int PackageLimit = 500);

public sealed record AssetSampleItem(
    AssetSampleType Type, string InputPath, string ObjectName, AssetSampleStatus Status, int FilesWritten);

public sealed record AssetSampleCoverage(AssetSampleType Type, int Requested, int Candidates,
    int Converted, int NoOutput, int Failed, AssetSampleCoverageStatus Status);

public sealed record AssetSampleResult(
    string OutputDirectory,
    int PackagesInspected,
    int DiscoveryFailures,
    bool PackageLimitReached,
    bool Cancelled,
    IReadOnlyList<AssetSampleItem> Items,
    IReadOnlyList<AssetSampleType> MissingTypes)
{
    public IReadOnlyList<AssetSampleCoverage> Coverage { get; init; } = [];
    public int OmittedItems { get; init; }
    public int OmittedErrorRows { get; init; }

    public int ExitCode => Cancelled || DiscoveryFailures > 0 || HasIncompleteCoverage ? 2 : 0;

    private bool HasIncompleteCoverage => Coverage.Count > 0
        ? Coverage.Any(type => type.Status != AssetSampleCoverageStatus.Complete || type.Failed > 0)
        : Items.Any(item => item.Status == AssetSampleStatus.Failed) ||
          !Items.Any(item => item.Status == AssetSampleStatus.Converted);
}

internal sealed class AssetSampleCoverageTracker(IEnumerable<AssetSampleType> types, int requested)
{
    private const int DetailsPerDiagnosticStatus = 10;
    private readonly Dictionary<AssetSampleType, Counts> _counts = types.ToDictionary(type => type, _ => new Counts());
    private readonly List<AssetSampleItem> _items = [];
    private int _omittedItems;

    private sealed class Counts
    {
        public int Converted, NoOutput, Failed;
        public int Candidates => Converted + NoOutput + Failed;
    }

    public bool IsComplete => _counts.Values.All(count => count.Converted == requested);

    public bool NeedsSample(AssetSampleType type) =>
        _counts.TryGetValue(type, out var count) && count.Converted < requested;

    public void Record(AssetSampleItem item)
    {
        var count = _counts[item.Type];
        var keep = item.Status switch
        {
            AssetSampleStatus.Converted => ++count.Converted <= requested,
            AssetSampleStatus.NoOutput => ++count.NoOutput <= DetailsPerDiagnosticStatus,
            AssetSampleStatus.Failed => ++count.Failed <= DetailsPerDiagnosticStatus,
            _ => throw new ArgumentOutOfRangeException(nameof(item))
        };
        if (keep) _items.Add(item);
        else _omittedItems++;
    }

    public AssetSampleResult Finish(string outputDirectory, int packagesInspected, int discoveryFailures,
        bool limitReached, bool cancelled, int omittedErrorRows)
    {
        AssetSampleCoverageStatus GetStatus(Counts count)
        {
            if (count.Converted == requested) return AssetSampleCoverageStatus.Complete;
            if (cancelled) return AssetSampleCoverageStatus.Cancelled;
            if (limitReached) return AssetSampleCoverageStatus.PackageLimitReached;
            if (count.Candidates == 0 && discoveryFailures == 0) return AssetSampleCoverageStatus.Missing;
            return AssetSampleCoverageStatus.Incomplete;
        }

        var coverage = _counts.Select(pair => new AssetSampleCoverage(pair.Key, requested, pair.Value.Candidates,
            pair.Value.Converted, pair.Value.NoOutput, pair.Value.Failed, GetStatus(pair.Value))).ToArray();
        return new AssetSampleResult(outputDirectory, packagesInspected, discoveryFailures, limitReached, cancelled,
            _items.ToArray(), coverage.Where(type => type.Candidates == 0).Select(type => type.Type).ToArray())
        {
            Coverage = coverage,
            OmittedItems = _omittedItems,
            OmittedErrorRows = omittedErrorRows
        };
    }
}

public static class AssetSampling
{
    public const string ReportName = "sample-report.json";
    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<AssetSampleResult> RunAsync(Options options, AssetSampleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (options.Mode != ExportMode.Full || !options.WriteAssets || options.DryRun)
            throw new UserFacingException("A sample needs the converted-files mode with asset conversion enabled.");
        if (request.PerType is < 1 or > 5 || request.PackageLimit is < 1 or > 10000)
            throw new UserFacingException("Choose 1 to 5 samples per type and a package limit from 1 to 10,000.");
        if (request.Type is { } type && !Enabled(options, type))
            throw new UserFacingException($"Enable {type.ToString().ToLowerInvariant()} export before running a sample.");

        var parent = string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? Path.Combine(Path.GetTempPath(), "UEBulkExport-Samples")
            : Path.GetFullPath(options.OutputDirectory);
        // Resolve mappings against the supplied context before assigning the fresh result directory.
        options.OutputDirectory = parent;
        Cli.Prepare(options);
        var runDirectory = Path.Combine(parent, $"sample-{Guid.NewGuid():N}");
        options.OutputDirectory = Path.Combine(runDirectory, "files");
        cancellationToken.ThrowIfCancellationRequested();

        Natives.Initialize(options);
        using var exporter = new BulkExporter(options);
        exporter.Mount();
        if (exporter.Containers.Any(container => container.IsLocked))
            throw new UserFacingException("Some containers are locked.", "Provide their AES keys before testing conversion.");

        var files = exporter.SelectFiles(cancellationToken);
        var result = await exporter.RunSampleAsync(files, request, cancellationToken);
        var report = result with { OutputDirectory = runDirectory };
        AtomicFile.WriteAllBytes(Path.Combine(runDirectory, ReportName),
            JsonSerializer.SerializeToUtf8Bytes(report, ReportOptions));
        return report;
    }

    internal static bool Enabled(Options options, AssetSampleType type) => type switch
    {
        AssetSampleType.Level => options.ExportWorlds,
        AssetSampleType.Material => options.ExportMaterials,
        _ => true
    };
}

public sealed partial class BulkExporter
{
    private const int MaximumSampleErrorRows = 100;
    private bool _collectingSampleErrors;
    private int _sampleErrorsSeen;

    private bool ShouldRecordSampleError() => !_collectingSampleErrors ||
        Interlocked.Increment(ref _sampleErrorsSeen) <= MaximumSampleErrorRows;

    internal async Task<AssetSampleResult> RunSampleAsync(IReadOnlyList<GameFile> files, AssetSampleRequest request,
        CancellationToken ct)
    {
        var types = request.Type is { } single ? [single] : Enum.GetValues<AssetSampleType>()
            .Where(type => AssetSampling.Enabled(_options, type)).ToArray();
        var coverage = new AssetSampleCoverageTracker(types, request.PerType);
        var packagesInspected = 0;
        var discoveryFailures = 0;
        var limitReached = false;
        var cancelled = false;
        _sampleErrorsSeen = 0;
        _collectingSampleErrors = true;

        try
        {
            Directory.CreateDirectory(_options.OutputDirectory);
            OpenStagingRun();
            foreach (var file in files.Where(file => file.IsUePackage))
            {
                ct.ThrowIfCancellationRequested();
                if (coverage.IsComplete) break;
                if (packagesInspected == request.PackageLimit)
                {
                    limitReached = true;
                    break;
                }

                packagesInspected++;
                UObject[] exports;
                try { exports = _provider.LoadPackage(file).GetExports().ToArray(); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    discoveryFailures++;
                    RecordError(file.Path, e, file.Path);
                    continue;
                }

                foreach (var export in exports)
                {
                    ct.ThrowIfCancellationRequested();
                    if (SampleTypeOf(export) is not { } type || !coverage.NeedsSample(type)) continue;
                    coverage.Record(await ConvertSampleAsync(file, export, type, ct));
                }
            }
            WriteErrorReport();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && _outputLock is not null)
        {
            cancelled = true;
            WriteErrorReport();
        }
        finally
        {
            _collectingSampleErrors = false;
            CloseStagingRun();
        }

        return coverage.Finish(_options.OutputDirectory, packagesInspected, discoveryFailures, limitReached,
            cancelled, Math.Max(0, _sampleErrorsSeen - MaximumSampleErrorRows));
    }

    private static AssetSampleType? SampleTypeOf(UObject export) => export switch
    {
        UTexture => AssetSampleType.Texture,
        UStaticMesh or USkeletalMesh or UGeometryCollection => AssetSampleType.Mesh,
        UAnimationAsset or USkeleton => AssetSampleType.Animation,
        USoundWave or UAkMediaAssetData => AssetSampleType.Audio,
        UWorld => AssetSampleType.Level,
        UMaterialInterface => AssetSampleType.Material,
        _ => null
    };

    private async Task<AssetSampleItem> ConvertSampleAsync(GameFile file, UObject export, AssetSampleType type,
        CancellationToken ct)
    {
        try
        {
            var result = type == AssetSampleType.Audio
                ? await ExportSoundsAsync([export], file, ct)
                : await RunSessionAsync(session => Queue(session, [export], KindOf(export)), file.Path, type switch
                {
                    AssetSampleType.Animation => _animExportOptions,
                    AssetSampleType.Level => _worldExportOptions,
                    _ => _exportOptions
                }, ct);
            var status = result.HadErrors ? AssetSampleStatus.Failed
                : result.Wrote ? AssetSampleStatus.Converted : AssetSampleStatus.NoOutput;
            return new AssetSampleItem(type, file.Path, export.Name, status, result.Outputs?.Count ?? 0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            RecordError($"{file.Path} :: {export.Name}", e, file.Path);
            return new AssetSampleItem(type, file.Path, export.Name, AssetSampleStatus.Failed, 0);
        }
    }
}
