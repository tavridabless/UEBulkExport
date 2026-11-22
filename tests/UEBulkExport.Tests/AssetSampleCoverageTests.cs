namespace UEBulkExport.Tests;

public sealed class AssetSampleCoverageTests
{
    [Fact]
    public void No_output_does_not_fill_the_quota_and_later_success_completes_coverage()
    {
        var tracker = new AssetSampleCoverageTracker([AssetSampleType.Animation], requested: 1);
        tracker.Record(Item(AssetSampleType.Animation, AssetSampleStatus.NoOutput));
        Assert.True(tracker.NeedsSample(AssetSampleType.Animation));
        Assert.False(tracker.IsComplete);

        tracker.Record(Item(AssetSampleType.Animation, AssetSampleStatus.Converted));
        var result = tracker.Finish("sample", 2, 0, false, false, 0);

        Assert.Equal(new AssetSampleCoverage(AssetSampleType.Animation, 1, 2, 1, 1, 0,
            AssetSampleCoverageStatus.Complete), Assert.Single(result.Coverage));
        Assert.True(tracker.IsComplete);
        Assert.False(tracker.NeedsSample(AssetSampleType.Animation));
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(false, false, 0, AssetSampleCoverageStatus.Missing)]
    [InlineData(false, false, 1, AssetSampleCoverageStatus.Incomplete)]
    [InlineData(true, false, 0, AssetSampleCoverageStatus.PackageLimitReached)]
    [InlineData(false, true, 0, AssetSampleCoverageStatus.Cancelled)]
    public void Search_stop_distinguishes_missing_types_from_unconfirmed_coverage(bool limit, bool cancelled,
        int discoveryFailures, AssetSampleCoverageStatus expected)
    {
        var tracker = new AssetSampleCoverageTracker([AssetSampleType.Texture, AssetSampleType.Mesh], requested: 1);
        tracker.Record(Item(AssetSampleType.Texture, AssetSampleStatus.Converted));

        var result = tracker.Finish("sample", 1, discoveryFailures, limit, cancelled, 0);

        Assert.Equal(AssetSampleCoverageStatus.Complete, result.Coverage[0].Status);
        Assert.Equal(expected, result.Coverage[1].Status);
        Assert.Equal([AssetSampleType.Mesh], result.MissingTypes);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Exhausted_search_reports_an_incomplete_quota_when_the_type_was_found()
    {
        var tracker = new AssetSampleCoverageTracker([AssetSampleType.Texture], requested: 2);
        tracker.Record(Item(AssetSampleType.Texture, AssetSampleStatus.Converted));

        var result = tracker.Finish("sample", 1, 0, false, false, 0);

        Assert.Equal(AssetSampleCoverageStatus.Incomplete, Assert.Single(result.Coverage).Status);
        Assert.Empty(result.MissingTypes);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Aggregates_include_omitted_diagnostics_and_keep_late_success()
    {
        var tracker = new AssetSampleCoverageTracker([AssetSampleType.Audio], requested: 1);
        for (var i = 0; i < 45; i++)
        {
            tracker.Record(Item(AssetSampleType.Audio, AssetSampleStatus.NoOutput));
            tracker.Record(Item(AssetSampleType.Audio, AssetSampleStatus.Failed));
        }
        tracker.Record(Item(AssetSampleType.Audio, AssetSampleStatus.Converted));

        var result = tracker.Finish("sample", 91, 0, false, false, 27);

        Assert.Equal(new AssetSampleCoverage(AssetSampleType.Audio, 1, 91, 1, 45, 45,
            AssetSampleCoverageStatus.Complete), Assert.Single(result.Coverage));
        Assert.Equal(21, result.Items.Count);
        Assert.Equal(70, result.OmittedItems);
        Assert.Equal(27, result.OmittedErrorRows);
        Assert.Equal(AssetSampleStatus.Converted, result.Items[^1].Status);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Sample_error_csv_is_bounded_without_dropping_package_failure_counts()
    {
        using var temp = new TempDir();
        var output = Path.Combine(temp.Path, "sample");
        using var exporter = new BulkExporter(new Options
        {
            PaksDirectory = temp.Dir("Paks"),
            OutputDirectory = output,
            Mode = ExportMode.Full
        });
        var packages = Enumerable.Range(0, 110)
            .Select(index => new SyntheticGameFile($"Game/Unreadable-{index}.uasset"))
            .ToArray();

        var result = await exporter.RunSampleAsync(packages, new AssetSampleRequest(AssetSampleType.Texture), default);

        Assert.Equal(110, result.PackagesInspected);
        Assert.Equal(110, result.DiscoveryFailures);
        Assert.Equal(10, result.OmittedErrorRows);
        Assert.Equal(101, File.ReadAllLines(Path.Combine(output, "errors.csv")).Length);
    }

    private static AssetSampleItem Item(AssetSampleType type, AssetSampleStatus status) =>
        new(type, "Game/asset.uasset", "Asset", status, status == AssetSampleStatus.Converted ? 1 : 0);
}
