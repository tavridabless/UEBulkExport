using System.Globalization;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Readers;

namespace UEBulkExport.Tests;

public sealed class RawInputByteBudgetTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Simultaneous_reservations_respect_the_budget_independently_of_worker_count()
    {
        var budget = new RawInputByteBudget(48);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Work()
        {
            using var lease = await budget.AcquireAsync(12);
            await release.Task;
        }

        // Calling the async workers starts every acquisition synchronously: four hold leases,
        // the remaining twenty queue. No timer, thread scheduling or sleep controls the check.
        var workers = Enumerable.Range(0, 24).Select(_ => Work()).ToArray();
        var blocked = budget.Snapshot();
        Assert.Equal(48, blocked.ReservedBytes);
        Assert.Equal(4, blocked.ActiveEntries);
        Assert.Equal(20, blocked.WaitingEntries);

        release.SetResult();
        await Task.WhenAll(workers).WaitAsync(Timeout);

        var completed = budget.Snapshot();
        Assert.Equal(48, completed.PeakReservedBytes);
        Assert.Equal(4, completed.PeakActiveEntries);
        Assert.Equal(0, completed.ReservedBytes);
        Assert.Equal(0, completed.ActiveEntries);
        Assert.Equal(0, completed.WaitingEntries);
    }

    [Fact]
    public async Task Oversized_head_is_not_starved_and_runs_alone_even_against_zero_size_entries()
    {
        var budget = new RawInputByteBudget(10);
        var initial = await budget.AcquireAsync(6);
        var oversized = budget.AcquireAsync(20).AsTask();
        var small = budget.AcquireAsync(1).AsTask();
        var empty = budget.AcquireAsync(0).AsTask();

        Assert.False(oversized.IsCompleted);
        Assert.False(small.IsCompleted); // Available 4 bytes cannot bypass the FIFO head.
        Assert.False(empty.IsCompleted);

        initial.Dispose();
        var largeLease = await oversized.WaitAsync(Timeout);
        Assert.Equal(20, budget.Snapshot().ReservedBytes);
        Assert.Equal(1, budget.Snapshot().ActiveEntries);
        Assert.False(small.IsCompleted);
        Assert.False(empty.IsCompleted);

        largeLease.Dispose();
        using var smallLease = await small.WaitAsync(Timeout);
        using var emptyLease = await empty.WaitAsync(Timeout);
        Assert.Equal(1, budget.Snapshot().ReservedBytes);
        Assert.Equal(2, budget.Snapshot().ActiveEntries);
        Assert.Equal(20, budget.Snapshot().PeakReservedBytes);
    }

    [Fact]
    public async Task Unknown_size_runs_alone_without_fabricating_source_bytes_and_preserves_fifo()
    {
        var budget = new RawInputByteBudget(10);
        var initial = await budget.AcquireAsync(4);
        var unknown = budget.AcquireUnknownSizeAsync().AsTask();
        var oversized = budget.AcquireAsync(20).AsTask();
        var empty = budget.AcquireAsync(0).AsTask();
        Assert.False(unknown.IsCompleted);

        initial.Dispose();
        var unknownLease = await unknown.WaitAsync(Timeout);
        var unknownState = budget.Snapshot();
        Assert.Equal(1, unknownState.UnknownSizeEntries);
        Assert.Equal(1, unknownState.ActiveEntries);
        Assert.Equal(0, unknownState.ReservedBytes);
        Assert.Equal(4, unknownState.PeakReservedBytes);
        Assert.False(oversized.IsCompleted);
        Assert.False(empty.IsCompleted);

        unknownLease.Dispose();
        var oversizedLease = await oversized.WaitAsync(Timeout);
        Assert.False(empty.IsCompleted);
        Assert.Equal(20, budget.Snapshot().ReservedBytes);
        oversizedLease.Dispose();
        using var emptyLease = await empty.WaitAsync(Timeout);
        Assert.Equal(1, budget.Snapshot().UnknownSizeEntries);
    }

    [Fact]
    public async Task Cancelled_unknown_size_waiter_does_not_consume_or_count_a_reservation()
    {
        var budget = new RawInputByteBudget(10);
        using var initial = await budget.AcquireAsync(4);
        using var cancellation = new CancellationTokenSource();
        var unknown = budget.AcquireUnknownSizeAsync(cancellation.Token).AsTask();
        var next = budget.AcquireAsync(6).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unknown.WaitAsync(Timeout));
        using var nextLease = await next.WaitAsync(Timeout);
        Assert.Equal(0, budget.Snapshot().UnknownSizeEntries);
        Assert.Equal(10, budget.Snapshot().ReservedBytes);
    }

    [Fact]
    public async Task Cancelling_a_blocked_head_unblocks_the_next_fitting_waiter()
    {
        var budget = new RawInputByteBudget(10);
        using var initial = await budget.AcquireAsync(6);
        using var cancellation = new CancellationTokenSource();
        var head = budget.AcquireAsync(8, cancellation.Token).AsTask();
        var next = budget.AcquireAsync(4).AsTask();
        Assert.False(next.IsCompleted);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head.WaitAsync(Timeout));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        using var nextLease = await next.WaitAsync(Timeout);

        Assert.Equal(10, budget.Snapshot().ReservedBytes);
        Assert.Equal(0, budget.Snapshot().WaitingEntries);
    }

    [Fact]
    public async Task Cancelling_all_waiters_leaves_no_pending_reservations_or_registrations()
    {
        var budget = new RawInputByteBudget(10);
        var initial = await budget.AcquireAsync(10);
        using var cancellation = new CancellationTokenSource();
        var waiters = Enumerable.Range(0, 12)
            .Select(_ => budget.AcquireAsync(2, cancellation.Token).AsTask()).ToArray();
        Assert.Equal(12, budget.Snapshot().WaitingEntries);

        cancellation.Cancel();
        foreach (var waiter in waiters)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Timeout));
        Assert.Equal(0, budget.Snapshot().WaitingEntries);
        initial.Dispose();

        using var replacement = await budget.AcquireAsync(10).AsTask().WaitAsync(Timeout);
        Assert.Equal(10, budget.Snapshot().ReservedBytes);
    }

    [Fact]
    public async Task Precancelled_acquisition_does_not_reserve_or_queue()
    {
        var budget = new RawInputByteBudget(10);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => budget.AcquireAsync(10, cancellation.Token).AsTask());

        Assert.Equal(0, budget.Snapshot().ReservedBytes);
        Assert.Equal(0, budget.Snapshot().WaitingEntries);
    }

    [Fact]
    public async Task Failure_inside_using_releases_the_lease_and_duplicate_dispose_is_harmless()
    {
        var budget = new RawInputByteBudget(10);
        RawInputByteBudget.Lease? failedLease = null;

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var lease = await budget.AcquireAsync(10);
            failedLease = lease;
            throw new IOException("synthetic read/write failure");
        });
        failedLease!.Dispose();
        Assert.Equal(0, budget.Snapshot().ReservedBytes);
        Assert.Equal(0, budget.Snapshot().ActiveEntries);

        using var next = await budget.AcquireAsync(10).AsTask().WaitAsync(Timeout);
        Assert.Equal(10, budget.Snapshot().ReservedBytes);
    }

    [Fact]
    public async Task Long_maximum_oversize_and_zero_size_tracking_do_not_overflow()
    {
        var budget = new RawInputByteBudget(1);
        var empty = await budget.AcquireAsync(0);
        var maximum = budget.AcquireAsync(long.MaxValue).AsTask();
        Assert.False(maximum.IsCompleted); // Even an empty entry must leave first.
        empty.Dispose();

        var maximumLease = await maximum.WaitAsync(Timeout);
        Assert.Equal(long.MaxValue, budget.Snapshot().ReservedBytes);
        var next = budget.AcquireAsync(1).AsTask();
        Assert.False(next.IsCompleted);
        maximumLease.Dispose();
        using var nextLease = await next.WaitAsync(Timeout);
        Assert.Equal(1, budget.Snapshot().ReservedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Gate_requires_a_positive_capacity(long capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawInputByteBudget(capacity));

    [Fact]
    public void Negative_entry_size_is_rejected_without_changing_state()
    {
        var budget = new RawInputByteBudget(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.AcquireAsync(-1));
        Assert.Equal(0, budget.Snapshot().ActiveEntries);
    }
}

public sealed class RawInputByteBudgetCliTests
{
    [Fact]
    public void Default_is_256_mib_and_not_repeated_in_the_generated_command()
    {
        var options = new Options();
        Assert.Equal(256L * 1048576, options.MaxInFlightBytes);
        Assert.DoesNotContain("--max-inflight-mb", Cli.ToArguments(options));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("64", 64L * 1048576)]
    [InlineData("1.5", 1572864L)]
    [InlineData("0.00000095367431640625", 1L)]
    [InlineData("8796093022207.99999904632568359375", long.MaxValue)]
    public void Valid_mib_sizes_parse_to_exact_bytes(string value, long expected) =>
        Assert.Equal(expected, Cli.ParseArguments(["--max-inflight-mb", value]).MaxInFlightBytes);

    [Theory]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("many")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,5")]
    [InlineData("0.1")]
    [InlineData("8796093022208")]
    [InlineData("9999999999999999999999999999")]
    [InlineData("1.2.3")]
    public void Malformed_fractional_byte_negative_and_overflow_sizes_are_user_facing(string value) =>
        Assert.Throws<UserFacingException>(() => Cli.ParseArguments(["--max-inflight-mb", value]));

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(67108865L)]
    [InlineData(long.MaxValue)]
    public void Generated_arguments_roundtrip_without_current_culture_or_precision_loss(long bytes)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var args = Cli.ToArguments(new Options { MaxInFlightBytes = bytes });
            Assert.Contains("--max-inflight-mb", args);
            Assert.Equal(bytes, Cli.ParseArguments(args.ToArray()).MaxInFlightBytes);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public void Missing_size_is_reported_and_programmatic_negative_budget_fails_before_path_discovery()
    {
        Assert.Throws<UserFacingException>(() => Cli.ParseArguments(["--max-inflight-mb"]));
        var error = Assert.Throws<UserFacingException>(() => Cli.Prepare(new Options { MaxInFlightBytes = -1 }));
        Assert.Contains("negative", error.Headline);
    }

    [Fact]
    public void Worker_or_budget_changes_do_not_invalidate_resume_profiles()
    {
        using var temp = new TempDir();
        var options = new Options { Mode = ExportMode.Raw, PaksDirectory = temp.Dir("Containers") };
        var profile = ResumeJournal.CreateProfile(options);
        options.Threads = 16;
        options.MaxInFlightBytes = 1048576;
        Assert.Equal(profile, ResumeJournal.CreateProfile(options));
    }
}

public sealed class RawInputByteBudgetExportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Raw_read_failure_releases_oversized_budget_and_successful_entries_are_checkpointed()
    {
        using var temp = new TempDir();
        var options = CreateOptions(temp, ExportMode.Raw, 64);
        using var exporter = new BulkExporter(options);
        GameFile[] entries =
        [
            new ReadableSyntheticFile("Game/fail.bin", 128, () => throw new IOException("synthetic failure")),
            new ReadableSyntheticFile("Game/good-a.bin", 32, () => [1, 2, 3]),
            new ReadableSyntheticFile("Game/good-b.bin", 32, () => [4, 5])
        ];

        var summary = await exporter.RunAsync(entries).WaitAsync(Timeout);

        Assert.Equal(1, summary.FailedEntries);
        Assert.Equal(2, summary.Exported);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(options.OutputDirectory, "Game", "good-a.bin")));
        Assert.Equal([4, 5], File.ReadAllBytes(Path.Combine(options.OutputDirectory, "Game", "good-b.bin")));
        var budget = Assert.IsType<RawInputBudgetState>(summary.RawInputBudget);
        Assert.Equal(128, budget.PeakReservedBytes);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(0, budget.ActiveEntries);
        Assert.Equal(0, budget.WaitingEntries);
        var done = ResumeJournal.Load(Path.Combine(options.OutputDirectory, "_completed.raw.txt"),
            options.OutputDirectory, ResumeJournal.CreateProfile(options));
        Assert.Equal(2, done.Count);
        Assert.DoesNotContain("Game/fail.bin", done);
    }

    [Fact]
    public async Task Raw_cancellation_in_read_releases_the_lease_and_does_not_checkpoint_failure()
    {
        using var temp = new TempDir();
        using var cancellation = new CancellationTokenSource();
        var options = CreateOptions(temp, ExportMode.Raw, 16);
        using var exporter = new BulkExporter(options);
        GameFile[] entries =
        [new ReadableSyntheticFile("Game/cancel.bin", 32, () =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        })];

        var summary = await exporter.RunAsync(entries, cancellation.Token).WaitAsync(Timeout);

        Assert.True(summary.Cancelled);
        Assert.Equal(0, summary.FailedEntries);
        Assert.Equal(0, summary.RawInputBudget!.ReservedBytes);
        Assert.Equal(0, summary.RawInputBudget.ActiveEntries);
        Assert.Empty(ResumeJournal.Load(Path.Combine(options.OutputDirectory, "_completed.raw.txt"),
            options.OutputDirectory, ResumeJournal.CreateProfile(options)));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public async Task Unknown_metadata_size_still_reads_and_writes_without_a_fabricated_byte_statistic(long size)
    {
        using var temp = new TempDir();
        var options = CreateOptions(temp, ExportMode.Raw, 8);
        using var exporter = new BulkExporter(options);
        GameFile[] entries = [new ReadableSyntheticFile("Game/unknown.bin", size, () => [1, 2, 3, 4])];

        var summary = await exporter.RunAsync(entries).WaitAsync(Timeout);

        Assert.Equal(0, summary.FailedEntries);
        Assert.Equal(1, summary.Exported);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(options.OutputDirectory, "Game", "unknown.bin")));
        var state = Assert.IsType<RawInputBudgetState>(summary.RawInputBudget);
        Assert.Equal(1, state.UnknownSizeEntries);
        Assert.Equal(1, state.PeakActiveEntries);
        Assert.Equal(0, state.PeakReservedBytes);
        Assert.Equal(0, state.ActiveEntries);
    }

    [Fact]
    public async Task Unknown_size_read_failure_releases_exclusivity_for_the_next_reader()
    {
        using var temp = new TempDir();
        var options = CreateOptions(temp, ExportMode.Raw, 8);
        using var exporter = new BulkExporter(options);
        GameFile[] entries =
        [
            new ReadableSyntheticFile("Game/unknown.bin", -1, () => throw new IOException("unknown-size failure")),
            new ReadableSyntheticFile("Game/next.bin", 4, () => [7])
        ];

        var summary = await exporter.RunAsync(entries).WaitAsync(Timeout);

        Assert.Equal(1, summary.FailedEntries);
        Assert.Equal(1, summary.Exported);
        Assert.Equal(1, summary.RawInputBudget!.UnknownSizeEntries);
        Assert.Equal(4, summary.RawInputBudget.PeakReservedBytes);
        Assert.Equal(0, summary.RawInputBudget.ActiveEntries);
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(options.OutputDirectory, "Game", "next.bin")));
    }

    [Theory]
    [InlineData(ExportMode.Raw, 0L)]
    [InlineData(ExportMode.Full, 1L)]
    [InlineData(ExportMode.Json, 1L)]
    [InlineData(ExportMode.Legacy, 1L)]
    public async Task Disabled_raw_budget_and_other_modes_do_not_claim_source_reservation_limits(
        ExportMode mode, long capacity)
    {
        using var temp = new TempDir();
        var options = CreateOptions(temp, mode, capacity);
        using var exporter = new BulkExporter(options);
        GameFile[] entries = [new ReadableSyntheticFile("Game/loose.bin", 16, () => [7, 8])];

        var summary = await exporter.RunAsync(entries).WaitAsync(Timeout);

        Assert.Null(summary.RawInputBudget);
        Assert.Equal(0, summary.FailedEntries);
    }

    [Fact]
    public void Dry_run_reports_oversized_work_without_writing_or_reading_entries()
    {
        using var temp = new TempDir();
        var options = CreateOptions(temp, ExportMode.Raw, 8);
        using var exporter = new BulkExporter(options);
        var plan = exporter.BuildPlan([new SyntheticGameFile("Game/a.bin", 6),
            new SyntheticGameFile("Game/b.bin", 12), new SyntheticGameFile("Game/unknown.bin", -1)]);

        Assert.Equal(8, plan.RawInputBudgetBytes);
        Assert.Equal(1, plan.RawInputOversizedEntries);
        Assert.Equal(1, plan.RawInputUnknownSizeEntries);
        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    private static Options CreateOptions(TempDir temp, ExportMode mode, long capacity) => new()
    {
        PaksDirectory = temp.Dir("Containers"),
        OutputDirectory = Path.Combine(temp.Path, "Export"),
        Mode = mode,
        Threads = 4,
        MaxInFlightBytes = capacity
    };

    private sealed class ReadableSyntheticFile(string path, long size, Func<byte[]> read) : GameFile(path, size)
    {
        public override bool IsEncrypted => false;
        public override CompressionMethod CompressionMethod => CompressionMethod.None;
        public override byte[] Read(FByteBulkDataHeader? header = null) => read();
        public override FArchive CreateReader(FByteBulkDataHeader? header = null) => throw new NotSupportedException();
    }
}
