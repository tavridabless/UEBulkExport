namespace UEBulkExport.Tests;

public sealed class ResumeJournalTests
{
    [Fact]
    public void Checkpoint_is_visible_immediately_and_requires_intact_outputs()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var output = temp.File("Export", "Game", "Content", "asset.uasset");
        File.WriteAllBytes(output, [1, 2, 3, 4]);
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.raw.txt");
        var profile = ResumeJournal.CreateProfile(options);

        using (var journal = new ResumeJournal(journalPath, options.OutputDirectory, profile, overwrite: true))
        {
            journal.MarkDone("Game/Content/asset.uasset", [output]);
            Assert.Contains("Game/Content/asset.uasset",
                ResumeJournal.Load(journalPath, options.OutputDirectory, profile));
        }

        File.WriteAllBytes(output, [1]);

        Assert.DoesNotContain("Game/Content/asset.uasset",
            ResumeJournal.Load(journalPath, options.OutputDirectory, profile));
    }

    [Fact]
    public void Incomplete_final_record_is_ignored_without_losing_earlier_checkpoints()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var output = temp.File("Export", "complete.bin");
        File.WriteAllBytes(output, [7, 8]);
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.raw.txt");
        var profile = ResumeJournal.CreateProfile(options);

        using (var journal = new ResumeJournal(journalPath, options.OutputDirectory, profile, overwrite: true))
            journal.MarkDone("complete.bin", [output]);
        File.AppendAllText(journalPath, "{\"Profile\":");

        var completed = ResumeJournal.Load(journalPath, options.OutputDirectory, profile);

        Assert.Contains("complete.bin", completed);
        Assert.Single(completed);
    }

    [Fact]
    public void A_record_written_after_a_torn_line_survives()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var output = temp.File("Export", "next.bin");
        File.WriteAllBytes(output, [5]);
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.raw.txt");
        var profile = ResumeJournal.CreateProfile(options);
        File.WriteAllText(journalPath, "{\"Profile\":");

        using (var journal = new ResumeJournal(journalPath, options.OutputDirectory, profile, overwrite: false))
            journal.MarkDone("next.bin", [output]);

        Assert.Contains("next.bin", ResumeJournal.Load(journalPath, options.OutputDirectory, profile));
    }

    [Fact]
    public void A_record_without_outputs_is_ignored()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.raw.txt");
        var profile = ResumeJournal.CreateProfile(options);
        File.WriteAllText(journalPath, $"{{\"Profile\":\"{profile}\",\"Path\":\"a.bin\"}}" + Environment.NewLine);

        Assert.Empty(ResumeJournal.Load(journalPath, options.OutputDirectory, profile));
    }

    [Fact]
    public void Checkpoints_from_a_different_export_profile_are_not_reused()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var output = temp.File("Export", "asset.json");
        File.WriteAllText(output, "{}");
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.full.txt");
        var oldProfile = ResumeJournal.CreateProfile(options);

        using (var journal = new ResumeJournal(journalPath, options.OutputDirectory, oldProfile, overwrite: true))
            journal.MarkDone("asset.uasset", [output]);

        options.ExportMaterials = true;
        var newProfile = ResumeJournal.CreateProfile(options);

        Assert.NotEqual(oldProfile, newProfile);
        Assert.Empty(ResumeJournal.Load(journalPath, options.OutputDirectory, newProfile));
    }

    [Fact]
    public void Legacy_path_only_checkpoints_are_redone_once_for_validation()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.raw.txt");
        File.WriteAllText(journalPath, "possibly-partial.bin" + Environment.NewLine);

        var completed = ResumeJournal.Load(journalPath, options.OutputDirectory,
            ResumeJournal.CreateProfile(options));

        Assert.Empty(completed);
    }

    [Fact]
    public void A_missing_declared_output_cannot_be_checkpointed()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.raw.txt");
        var missing = Path.Combine(options.OutputDirectory, "missing.bin");

        using var journal = new ResumeJournal(journalPath, options.OutputDirectory,
            ResumeJournal.CreateProfile(options), overwrite: true);

        Assert.Throws<IOException>(() => journal.MarkDone("missing.bin", [missing]));
    }

    [Fact]
    public async Task Concurrent_checkpoints_are_complete_and_visible_before_dispose()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var profile = ResumeJournal.CreateProfile(options);
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.raw.txt");
        var paths = Enumerable.Range(0, 64).Select(index => temp.File("Export", index + ".bin")).ToArray();
        foreach (var path in paths) File.WriteAllBytes(path, [1, 2, 3]);
        using var journal = new ResumeJournal(journalPath, options.OutputDirectory, profile, overwrite: true);

        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length), new ParallelOptions { MaxDegreeOfParallelism = 4 },
            (index, _) => { journal.MarkDone(index + ".bin", [paths[index]]); return ValueTask.CompletedTask; });

        var completed = ResumeJournal.Load(journalPath, options.OutputDirectory, profile);
        Assert.Equal(64, completed.Count);
        for (var index = 0; index < paths.Length; index++) Assert.Contains(index + ".bin", completed);
    }

    [Fact]
    public void One_missing_artifact_does_not_append_a_partial_checkpoint()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var profile = ResumeJournal.CreateProfile(options);
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.raw.txt");
        var existing = temp.File("Export", "existing.bin");
        File.WriteAllBytes(existing, [1]);
        var missing = Path.Combine(options.OutputDirectory, "missing.bin");
        using var journal = new ResumeJournal(journalPath, options.OutputDirectory, profile, overwrite: true);

        journal.MarkDone("complete.bin", [existing]);
        Assert.Throws<IOException>(() => journal.MarkDone("incomplete.bin", [existing, missing]));

        var completed = ResumeJournal.Load(journalPath, options.OutputDirectory, profile);
        Assert.Equal(["complete.bin"], completed);
    }

    [Fact]
    public void A_long_record_crossing_writer_and_stream_buffers_is_immediately_readable()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var profile = ResumeJournal.CreateProfile(options);
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.raw.txt");
        var paths = Enumerable.Range(0, 96).Select(index => temp.File("Export", "Unicode_Папка",
            $"long-output-name-for-buffer-boundary-{index:D4}.json")).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "{}");
        using var journal = new ResumeJournal(journalPath, options.OutputDirectory, profile, overwrite: true);

        journal.MarkDone("Unicode_Пакет.uasset", paths.Concat(paths));

        Assert.Contains("Unicode_Пакет.uasset", ResumeJournal.Load(journalPath, options.OutputDirectory, profile));
        File.WriteAllBytes(paths[^1], [1]);
        Assert.Empty(ResumeJournal.Load(journalPath, options.OutputDirectory, profile));
    }

    [Fact]
    public void Artifact_outside_output_is_rejected_without_recording_completion()
    {
        using var temp = new TempDir();
        var options = OptionsFor(temp);
        var profile = ResumeJournal.CreateProfile(options);
        var journalPath = Path.Combine(options.OutputDirectory, "_completed.raw.txt");
        var outside = temp.File("Outside", "asset.bin");
        using var journal = new ResumeJournal(journalPath, options.OutputDirectory, profile, overwrite: true);

        Assert.Throws<IOException>(() => journal.MarkDone("asset.bin", [outside]));
        Assert.Empty(ResumeJournal.Load(journalPath, options.OutputDirectory, profile));
    }

    private static Options OptionsFor(TempDir temp)
    {
        var paks = temp.Dir("Paks");
        File.WriteAllBytes(Path.Combine(paks, "test.pak"), [1]);
        return new Options
        {
            PaksDirectory = paks,
            OutputDirectory = temp.Dir("Export"),
            Mode = ExportMode.Raw
        };
    }
}
