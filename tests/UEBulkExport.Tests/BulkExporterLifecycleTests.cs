namespace UEBulkExport.Tests;

public sealed class BulkExporterLifecycleTests
{
    [Theory]
    [InlineData(ExportMode.Full)]
    [InlineData(ExportMode.Json)]
    public async Task Selected_raw_payload_is_written_even_when_loose_files_are_disabled(ExportMode mode)
    {
        using var temp = new TempDir();
        var options = CreateOptions(temp);
        options.Mode = mode;
        options.WriteRawPackages = true;
        options.WriteRawMisc = false;
        using var exporter = new BulkExporter(options);
        var payload = new SyntheticGameFile("Game/mesh.uexp", 2, () => [7, 9]);
        var loose = new SyntheticGameFile("Game/loose.ini");

        Assert.Equal(1, exporter.BuildPlan([payload, loose]).Payloads);
        var summary = await exporter.RunAsync([payload, loose]);

        Assert.Equal(1, summary.Exported);
        Assert.Equal(0, summary.FailedEntries);
        Assert.Equal([7, 9], File.ReadAllBytes(Path.Combine(options.OutputDirectory, "Game", "mesh.uexp")));
        Assert.False(File.Exists(Path.Combine(options.OutputDirectory, "Game", "loose.ini")));
    }

    [Fact]
    public async Task Run_checks_resume_artifacts_again_after_preview()
    {
        using var temp = new TempDir();
        var options = CreateOptions(temp);
        var entry = new SyntheticGameFile("Game/one.bin", 1, () => [3]);
        using (var first = new BulkExporter(options)) await first.RunAsync([entry]);

        using var second = new BulkExporter(options);
        Assert.Equal(1, second.BuildPlan([entry]).AlreadyDone);
        var path = Path.Combine(options.OutputDirectory, "Game", "one.bin");
        File.Delete(path);

        var summary = await second.RunAsync([entry]);

        Assert.Equal(1, summary.Exported);
        Assert.Equal([3], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Invalid_journal_destination_releases_output_lock()
    {
        using var temp = new TempDir();
        var options = CreateOptions(temp);
        var blockingDirectory = Directory.CreateDirectory(Path.Combine(options.OutputDirectory, "_completed.raw.txt"));
        using var first = new BulkExporter(options);
        var error = await Record.ExceptionAsync(() => first.RunAsync([]));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Directory.Delete(blockingDirectory.FullName);

        using var second = new BulkExporter(options);
        Assert.Equal(0, (await second.RunAsync([])).FailedEntries);
    }

    [Fact]
    public async Task Error_report_preserves_quotes_and_commas()
    {
        using var temp = new TempDir();
        var options = CreateOptions(temp);
        using var exporter = new BulkExporter(options);
        var file = new SyntheticGameFile("Game/file,name.bin", read: () =>
            throw new IOException("Cannot read \"file\", missing bytes."));

        var summary = await exporter.RunAsync([file]);
        Assert.Equal(1, summary.FailedEntries);
        Assert.Equal(["Game/file,name.bin"], summary.FailedInputPaths);

        Assert.Equal(["path,exception,message",
                "\"Game/file,name.bin\",\"IOException\",\"Cannot read \"\"file\"\", missing bytes.\""],
            File.ReadAllLines(Path.Combine(options.OutputDirectory, "errors.csv")));
    }

    [Fact]
    public async Task Root_diagnostics_name_is_reserved_for_report()
    {
        using var temp = new TempDir();
        var options = CreateOptions(temp);
        using var exporter = new BulkExporter(options);
        var file = new SyntheticGameFile("errors.csv", 3, () => [1, 2, 3]);

        var summary = await exporter.RunAsync([file]);

        Assert.Equal(0, summary.Exported);
        Assert.Equal(1, summary.FailedEntries);
        Assert.StartsWith("path,exception,message", File.ReadAllText(Path.Combine(options.OutputDirectory, "errors.csv")));
    }

    [Fact]
    public void Cancelled_file_selection_stops_before_enumeration()
    {
        using var temp = new TempDir();
        using var exporter = new BulkExporter(CreateOptions(temp));
        Assert.Throws<OperationCanceledException>(() => exporter.SelectFiles(new CancellationToken(canceled: true)));
    }

    private static Options CreateOptions(TempDir temp) => new()
    {
        PaksDirectory = temp.Dir("Paks"),
        OutputDirectory = Path.Combine(temp.Path, "Export"),
        Mode = ExportMode.Raw,
        Threads = 1
    };
}
