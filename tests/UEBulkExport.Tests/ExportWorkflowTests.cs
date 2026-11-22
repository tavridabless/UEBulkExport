using UEBulkExport.Gui.Services;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Tests;

public sealed class ExportWorkflowTests
{
    [Fact]
    public async Task Asset_sample_uses_a_new_output_context_and_preserves_selection()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.SelectedMode = vm.Modes.Single(mode => mode.Mode == ExportMode.Full);
        vm.SelectedSampleType = vm.SampleTypes.Single(type => type.Type == AssetSampleType.Texture);
        vm.SamplesPerType = 2;
        vm.SetSelection(new HashSet<string> { "Root/texture.uasset" });
        var output = vm.OutputPath;

        await vm.RunSampleCommand.ExecuteAsync(null);

        Assert.Equal(new AssetSampleRequest(AssetSampleType.Texture, 2), fixture.Backend.SampleRequest);
        Assert.Empty(fixture.Backend.SampleOptions!.OutputDirectory);
        Assert.Null(fixture.Backend.SampleOptions.PathsFile);
        Assert.Equal(["Root/texture.uasset"], fixture.Backend.SampleOptions.SelectedPaths);
        Assert.Equal(output, vm.OutputPath);
        Assert.False(Directory.Exists(output));
        Assert.True(vm.HasSample);
        Assert.Equal(0, fixture.Backend.RunCount);
        Assert.Equal(RunState.Idle, vm.State);
    }

    [Fact]
    public void Resource_presets_only_change_threads_and_raw_input_budget()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.SelectedMode = vm.Modes.Single(m => m.Mode == ExportMode.Full);
        vm.IncludeRegex = "^Root/";
        vm.Overwrite = true;
        var original = vm.ToOptions();
        vm.UseQuietProfileCommand.Execute(null);
        Assert.Equal(1, vm.Threads);
        Assert.Equal(64 * 1024 * 1024, vm.ToOptions().MaxInFlightBytes);
        vm.UseBalancedProfileCommand.Execute(null);
        Assert.InRange(vm.Threads, 1, 4);
        Assert.Equal(256, vm.MaxInFlightMiB);
        vm.UseParallelProfileCommand.Execute(null);
        Assert.InRange(vm.Threads, 1, vm.MaxThreads);
        Assert.Equal(512, vm.MaxInFlightMiB);
        var current = vm.ToOptions();
        Assert.Equal(original.Mode, current.Mode);
        Assert.Equal(original.MeshFormat, current.MeshFormat);
        Assert.Equal(original.IncludeRegex, current.IncludeRegex);
        Assert.Equal(original.Resume, current.Resume);
        vm.MaxInFlightMiB = 0;
        Assert.Equal(0, vm.ToOptions().MaxInFlightBytes);
    }

    [Fact]
    public async Task Plan_with_inline_selection_does_not_create_output_or_selection_file()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.SetSelection(new HashSet<string> { "Root/one.bin" });
        await vm.DryRunCommand.ExecuteAsync(null);
        Assert.NotNull(fixture.Backend.PlanOptions);
        Assert.Null(fixture.Backend.PlanOptions.PathsFile);
        Assert.Equal(["Root/one.bin"], fixture.Backend.PlanOptions.SelectedPaths);
        Assert.False(Directory.Exists(vm.OutputPath));
        Assert.Equal(RunState.Idle, vm.State);
    }

    [Fact]
    public void Changing_source_invalidates_both_browser_rows_and_export_selection()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        using var browser = new BrowserViewModel();
        vm.SourceChanged += browser.Clear;
        browser.Load(new ScanResult(vm.PaksPath, [], [new SyntheticGameFile("Root/one.bin")]));
        browser.Rows[0].IsChecked = true;
        browser.ExportRequested += vm.SetSelection;
        browser.ExportSelectedCommand.Execute(null);
        Assert.True(vm.HasSelection);
        Assert.Contains("--paths-file", vm.CommandLine);

        vm.PaksPath = "different-source";

        Assert.False(vm.HasSelection);
        Assert.Null(vm.ToOptions().SelectedPaths);
        Assert.DoesNotContain("--paths-file", vm.CommandLine);
        Assert.False(browser.HasData);
        Assert.False(browser.HasSelection);
        Assert.Empty(browser.Rows);
    }

    [Fact]
    public async Task Cancelled_scan_returns_to_idle_and_can_be_retried()
    {
        using var fixture = new Fixture();
        using var source = new TempDir();
        source.File("fixture.pak");
        using var vm = fixture.Create();
        vm.PaksPath = source.Path;

        var scanning = vm.AutoScanAsync();
        await fixture.Backend.ScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunState.Scanning, vm.State);
        vm.CancelCommand.Execute(null);
        await scanning;

        Assert.Equal(RunState.Idle, vm.State);
        Assert.Equal(ScanPhase.Cancelled, vm.ScanPhase);
        Assert.True(vm.CanRescan);
        Assert.True(vm.RescanCommand.CanExecute(null));
        Assert.Empty(vm.ScanError);
    }

    [Fact]
    public async Task Continue_uses_frozen_parameters_and_selection_not_current_form_or_backend_mutations()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        var selected = new HashSet<string> { "Root/one.bin" };
        vm.SetSelection(selected);
        selected.Add("Root/unwanted.bin");
        vm.Threads = 2;
        vm.MaxInFlightMiB = 64;
        vm.Overwrite = true;
        vm.IncludeRegex = "one";
        var originalOutput = vm.OutputPath;
        var first = vm.StartCommand.ExecuteAsync(null);
        Assert.False(vm.CanContinuePrevious);
        Assert.False(vm.ShowPreviousRun);
        Assert.False(vm.UseQuietProfileCommand.CanExecute(null));
        fixture.Backend.Options!.Threads = 99;
        ((HashSet<string>)fixture.Backend.Options.SelectedPaths!).Add("Root/backend-mutation.bin");
        vm.CancelCommand.Execute(null);
        await first;
        Assert.True(vm.CanContinuePrevious);
        Assert.True(vm.ShowPreviousRun);
        vm.OutputPath = Path.Combine(fixture.Root, "different-output");
        vm.PaksPath = "different-source";
        vm.SelectedMode = vm.Modes.Single(m => m.Mode == ExportMode.Json);
        vm.Threads = 7;
        vm.MaxInFlightMiB = 0;
        vm.IncludeRegex = "different";
        vm.SetSelection(new HashSet<string> { "Root/different.bin" });
        var resumed = vm.ContinuePreviousCommand.ExecuteAsync(null);
        var options = fixture.Backend.Options!;
        Assert.Equal(2, options.Threads);
        Assert.Equal(64L * 1024 * 1024, options.MaxInFlightBytes);
        Assert.True(options.Resume);
        Assert.Equal("synthetic-source", options.PaksDirectory);
        Assert.Equal(originalOutput, options.OutputDirectory);
        Assert.Equal(ExportMode.Raw, options.Mode);
        Assert.Equal("one", options.IncludeRegex);
        Assert.Equal(["Root/one.bin"], options.SelectedPaths);
        Assert.Equal(Path.Combine(originalOutput, "_selection.txt"), options.PathsFile);
        Assert.False(Directory.Exists(vm.OutputPath));
        fixture.Backend.Complete(cancelled: false);
        await resumed;
        Assert.False(vm.HasPreviousRun);
        Assert.Equal(RunState.Done, vm.State);
        Assert.Equal(originalOutput, vm.LastOutputDirectory);
    }

    [Fact]
    public async Task Invalid_legacy_iostore_selection_produces_no_output_files()
    {
        using var source = new TempDir();
        using var work = new TempDir();
        source.File("content.utoc");
        var output = Path.Combine(work.Path, "out");
        var service = new ExportService();
        var exception = await Assert.ThrowsAsync<UserFacingException>(() => service.ExportAsync(new Options
        {
            Mode = ExportMode.Legacy,
            PaksDirectory = source.Path,
            OutputDirectory = output,
            SelectedPaths = new HashSet<string> { "Root/asset.uasset" },
            PathsFile = Path.Combine(output, "_selection.txt")
        }));
        Assert.Contains("legacy IoStore", exception.Headline);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Legacy_iostore_form_blocks_selective_export_and_can_switch_to_raw()
    {
        using var fixture = new Fixture();
        using var source = new TempDir();
        source.File("content.utoc");
        using var vm = fixture.Create();
        vm.PaksPath = source.Path;
        vm.SelectedMode = vm.Modes.Single(mode => mode.Mode == ExportMode.Legacy);
        var scan = vm.AutoScanAsync();
        await fixture.Backend.ScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Backend.CompleteScan(source.Path);
        await scan;
        vm.SetSelection(new HashSet<string> { "Root/asset.uasset" });
        Assert.True(vm.IsReadinessBlocked);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.SwitchToRawCommand.CanExecute(null));
        vm.SwitchToRawCommand.Execute(null);
        Assert.True(vm.IsRawMode);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal(["Root/asset.uasset"], vm.ToOptions().SelectedPaths);
    }

    [Fact]
    public async Task Failure_retains_request_and_success_removes_it()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        var first = vm.StartCommand.ExecuteAsync(null);
        fixture.Backend.Fail(new IOException("synthetic backend failure"));
        await first;
        Assert.Equal(RunState.Failed, vm.State);
        Assert.True(vm.HasPreviousRun);
        var resumed = vm.ContinuePreviousCommand.ExecuteAsync(null);
        fixture.Backend.Complete(cancelled: false);
        await resumed;
        Assert.False(vm.CanContinuePrevious);
    }

    [Fact]
    public async Task Failed_files_can_be_selected_without_rerunning_or_changing_options()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        vm.IncludeRegex = "^Root/";
        var first = vm.StartCommand.ExecuteAsync(null);
        fixture.Backend.Complete(cancelled: false, failedInputPaths: ["Root/fail.bin"]);
        await first;

        Assert.True(vm.PrepareFailedSelectionCommand.CanExecute(null));
        vm.PrepareFailedSelectionCommand.Execute(null);

        Assert.Equal(1, fixture.Backend.RunCount);
        Assert.Equal(["Root/fail.bin"], vm.ToOptions().SelectedPaths);
        Assert.Equal("^Root/", vm.IncludeRegex);
        Assert.True(vm.HasPreviousRun);

        vm.PaksPath = "different-source";
        Assert.False(vm.PrepareFailedSelectionCommand.CanExecute(null));
        Assert.Null(vm.ToOptions().SelectedPaths);
    }

    [Fact]
    public async Task Preparation_failure_replaces_an_older_snapshot_instead_of_resuming_the_wrong_job()
    {
        using var fixture = new Fixture();
        using var vm = fixture.Create();
        var first = vm.StartCommand.ExecuteAsync(null);
        vm.CancelCommand.Execute(null);
        await first;
        Directory.CreateDirectory(fixture.Root);
        var blockedOutput = Path.Combine(fixture.Root, "existing-file");
        File.WriteAllText(blockedOutput, "fixture");
        vm.OutputPath = blockedOutput;
        vm.Threads = 3;
        vm.SetSelection(new HashSet<string> { "Root/new-job.bin" });
        var rejected = vm.StartCommand.ExecuteAsync(null);
        fixture.Backend.Fail(new IOException("Destination is not a directory."));
        await rejected;
        Assert.Equal(RunState.Failed, vm.State);
        Assert.Contains(blockedOutput, vm.PreviousRunText);
        var retried = vm.ContinuePreviousCommand.ExecuteAsync(null);
        Assert.Equal(blockedOutput, fixture.Backend.Options!.OutputDirectory);
        Assert.Equal(["Root/new-job.bin"], fixture.Backend.Options.SelectedPaths);
        fixture.Backend.Fail(new IOException("Destination is not a directory."));
        await retried;
        Assert.Equal(RunState.Failed, vm.State);
        Assert.Equal(3, fixture.Backend.RunCount);
    }

    [Fact]
    public async Task Progress_posted_after_completion_is_ignored()
    {
        using var fixture = new Fixture();
        var posted = new Queue<Action>();
        using var vm = fixture.Create(posted.Enqueue);
        var run = vm.StartCommand.ExecuteAsync(null);
        fixture.Backend.Emit(new ExportProgress(1, 10, 1, 0, TimeSpan.FromSeconds(1), null, "exporting")
            { RawInputBudget = new RawInputBudgetState(64, 32, 32, 1, 1, 2) });
        Assert.Single(posted);
        fixture.Backend.Complete(cancelled: false);
        await run;
        posted.Dequeue()();
        Assert.Equal(RunState.Done, vm.State);
        Assert.Equal("", vm.ProgressProcessed);
        Assert.False(vm.HasRawBudgetProgress);
    }

    [Fact]
    public async Task Dispose_cancels_unsubscribes_and_ignores_a_queued_progress_callback()
    {
        using var fixture = new Fixture();
        var posted = new Queue<Action>();
        var vm = fixture.Create(posted.Enqueue);
        var run = vm.StartCommand.ExecuteAsync(null);
        fixture.Backend.Emit(new ExportProgress(1, 10, 1, 0, TimeSpan.Zero, null, "exporting"));
        vm.Dispose();
        vm.Dispose();
        await run;
        posted.Dequeue()();
        Assert.Equal(0, fixture.Backend.SubscriberCount);
        Assert.Equal(1, fixture.Backend.CancelCount);
        Assert.False(vm.CanStart);
        Assert.False(vm.CanContinuePrevious);
        Assert.Equal("", vm.ProgressProcessed);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Backend.RunCount);
    }

    [Fact]
    public void Backend_snapshot_keeps_budget_and_does_not_share_mutable_collections()
    {
        var original = new Options { MaxInFlightBytes = 31, AesKeys = ["example"],
            SelectedPaths = new HashSet<string> { "Root/one" } };
        var copy = ExportService.Clone(original);
        original.AesKeys.Clear();
        ((HashSet<string>)original.SelectedPaths).Clear();
        Assert.Equal(31, copy.MaxInFlightBytes);
        Assert.Equal(["example"], copy.AesKeys);
        Assert.Equal(["Root/one"], copy.SelectedPaths);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "uebulkexport-workflow-" + Guid.NewGuid().ToString("N"));
        public FakeBackend Backend { get; } = new();
        public ExportViewModel Create(Action<Action>? post = null)
        {
            var vm = new ExportViewModel(new AppSettings { RememberPaths = false }, Backend,
                post ?? (action => action()), autoScan: false, persistPreferences: false, trackPerformance: false)
                { PaksPath = "synthetic-source", OutputPath = Path.Combine(Root, "output") };
            vm.SelectedMode = vm.Modes.Single(m => m.Mode == ExportMode.Raw);
            return vm;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private sealed class FakeBackend : IExportService
    {
        private Action<ExportProgress>? _progress;
        private TaskCompletionSource<ScanResult>? _scanCompletion;
        private TaskCompletionSource<ExportSummary>? _completion;
        public event Action<ExportProgress>? ProgressChanged { add => _progress += value; remove => _progress -= value; }
        public int SubscriberCount => _progress?.GetInvocationList().Length ?? 0;
        public Options? Options { get; private set; }
        public Options? PlanOptions { get; private set; }
        public Options? SampleOptions { get; private set; }
        public AssetSampleRequest? SampleRequest { get; private set; }
        public int CancelCount { get; private set; }
        public int RunCount { get; private set; }
        public TaskCompletionSource ScanStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ScanResult> ScanAsync(Options options)
        {
            _scanCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ScanStarted.TrySetResult();
            return _scanCompletion.Task;
        }
        public void CompleteScan(string source) => _scanCompletion!.TrySetResult(new ScanResult(source, [], []));
        public Task<ExportPlan> DryRunAsync(Options options)
        {
            PlanOptions = options;
            return Task.FromResult(new ExportPlan(options.Mode, options.OutputDirectory, null, 1, 0, 0, 0, 1, 0, 0)
                { RawInputBudgetBytes = options.MaxInFlightBytes });
        }
        public Task<ExportSummary> ExportAsync(Options options)
        {
            Options = options;
            RunCount++;
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _completion.Task;
        }
        public Task<AssetSampleResult> SampleAsync(Options options, AssetSampleRequest request)
        {
            SampleOptions = options;
            SampleRequest = request;
            return Task.FromResult(new AssetSampleResult("sample-output", 1, 0, false, false, [], []));
        }
        public void Emit(ExportProgress progress) => _progress?.Invoke(progress);
        public void Complete(bool cancelled, string[]? failedInputPaths = null) =>
            _completion!.TrySetResult(new ExportSummary(TimeSpan.FromSeconds(1),
                1, 1, 1, 0, 0, 0, failedInputPaths?.Length ?? 0, 0, Options!.OutputDirectory, cancelled)
            {
                FailedInputPaths = failedInputPaths ?? []
            });
        public void Fail(Exception error) => _completion!.TrySetException(error);
        public void Cancel()
        {
            CancelCount++;
            _scanCompletion?.TrySetCanceled();
            if (_completion is { Task.IsCompleted: false }) Complete(cancelled: true);
        }
    }
}
