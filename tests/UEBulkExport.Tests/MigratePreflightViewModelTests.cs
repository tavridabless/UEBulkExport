using UEBulkExport.Gui.Services;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Tests;

public sealed class MigratePreflightViewModelTests
{
    private static DumpConversionPreflight Plan(params DumpConversionDiagnostic[] diagnostics) =>
        new(1, 0, 1, 0, 0, 0, 0, 20, "5.5", "/Game/Plan", "planned-only", diagnostics);

    private static AppSettings Settings(TempDir temp)
    {
        var project = temp.File("Target", "Target.uproject");
        System.IO.File.WriteAllText(project, "{\"EngineAssociation\":\"5.5\"}");
        return new AppSettings
        {
            ConversionSourcePath = temp.Dir("Dump"),
            ConversionSourceVersion = "4.27",
            TargetProjectPath = project,
            UnrealEditorPath = temp.File("Tools", "UnrealEditor-Cmd.exe"),
            UModelPath = temp.File("Tools", "umodel.exe")
        };
    }

    [Fact]
    public async Task Preview_is_single_flight_and_cancel_discards_an_uncooperative_late_plan()
    {
        using var temp = new TempDir();
        var completion = new TaskCompletionSource<DumpConversionPreflight>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken token = default;
        DumpConversionOptions? captured = null;
        using var vm = new MigrateViewModel(Settings(temp), (options, cancellation) =>
        {
            calls++;
            captured = options;
            token = cancellation;
            return completion.Task;
        });
        var source = vm.SourcePath;
        var running = vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.IsPreviewing);
        Assert.False(vm.CanStart);
        Assert.False(vm.CanEditInputs);
        Assert.True(vm.CanCancel);
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
        Assert.Equal(source, captured!.SourceDirectory);
        vm.CancelCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        completion.SetResult(Plan());
        await running;
        Assert.False(vm.IsPreviewing);
        Assert.False(vm.HasPreflight);
        Assert.True(vm.CanPreview);
        Assert.False(vm.CanCancel);
    }

    [Fact]
    public async Task Changed_inputs_cancel_the_check_and_invalidate_an_existing_plan()
    {
        using var temp = new TempDir();
        var completion = new TaskCompletionSource<DumpConversionPreflight>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken token = default;
        using var vm = new MigrateViewModel(Settings(temp), (_, cancellation) =>
        {
            token = cancellation;
            return ++calls == 1 ? Task.FromResult(Plan()) : completion.Task;
        });
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.HasPreflight);
        Assert.True(vm.PreflightCanProceed);
        vm.DestinationPath = "/Game/Changed";
        Assert.False(vm.HasPreflight);
        Assert.Empty(vm.PreflightSummary);
        var pending = vm.PreviewCommand.ExecuteAsync(null);
        vm.SourcePath = temp.Dir("ChangedDump");
        Assert.True(token.IsCancellationRequested);
        completion.SetResult(Plan());
        await pending;
        Assert.False(vm.HasPreflight);
        Assert.Equal(temp.Dir("ChangedDump"), vm.SourcePath);
    }

    [Fact]
    public async Task Target_version_getters_use_the_cache_until_a_fresh_check_updates_it()
    {
        using var temp = new TempDir();
        var settings = Settings(temp);
        using var vm = new MigrateViewModel(settings, autoDetect: false);
        Assert.Equal("5.5", vm.TargetVersion);
        System.IO.File.WriteAllText(settings.TargetProjectPath, "{\"EngineAssociation\":\"5.6\"}");
        for (var count = 0; count < 20; count++)
        {
            Assert.Equal("5.5", vm.TargetVersion);
            Assert.Contains("5.5", vm.TargetVersionText);
            _ = vm.ToolsSummary;
            _ = vm.ReadinessState;
        }
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal("5.6", vm.TargetVersion);
        Assert.True(vm.HasPreflight);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(settings.TargetProjectPath)!, "Saved")));
    }

    [Fact]
    public async Task Plan_requirement_errors_block_migration_until_inputs_are_rechecked()
    {
        using var temp = new TempDir();
        using var vm = new MigrateViewModel(Settings(temp), (_, _) =>
            Task.FromResult(Plan(new DumpConversionDiagnostic(DumpConversionDiagnosticLevel.Error, "PythonDisabled"))));
        Assert.True(vm.CanStart);
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.HasPreflight);
        Assert.False(vm.PreflightCanProceed);
        Assert.False(vm.CanStart);
        Assert.Equal(Readiness.Blocked, vm.ReadinessState.Level);
        Assert.True(Assert.Single(vm.PreflightDiagnostics).IsError);
        vm.DestinationPath = "/Game/Changed";
        Assert.False(vm.HasPreflight);
        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task Confirmation_reserves_the_request_and_changed_inputs_do_not_start_a_different_migration()
    {
        using var temp = new TempDir();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = new MigrateViewModel(Settings(temp), autoDetect: false);
        var calls = 0;
        vm.Confirm = (_, _) => { calls++; return confirmation.Task; };
        var first = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirming);
        Assert.False(vm.CanEditInputs);
        Assert.False(vm.CanStart);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
        vm.DestinationPath = "/Game/ChangedWhileDialogOpen";
        confirmation.SetResult(true);
        await first;
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsConfirming);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(vm.TargetProjectPath)!, "Saved")));
    }

    [Fact]
    public async Task Dispose_cancels_preview_and_suppresses_late_results()
    {
        using var temp = new TempDir();
        var completion = new TaskCompletionSource<DumpConversionPreflight>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var vm = new MigrateViewModel(Settings(temp), (_, cancellation) => { token = cancellation; return completion.Task; });
        var running = vm.PreviewCommand.ExecuteAsync(null);
        vm.Dispose();
        Assert.True(token.IsCancellationRequested);
        completion.SetResult(Plan());
        await running;
        Assert.False(vm.HasPreflight);
        Assert.False(vm.CanStart);
        Assert.False(vm.CanPreview);
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_releases_a_pending_confirmation_without_starting_tools()
    {
        using var temp = new TempDir();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MigrateViewModel(Settings(temp), autoDetect: false) { Confirm = (_, _) => confirmation.Task };
        var running = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirming);
        vm.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.IsBusy);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(vm.TargetProjectPath)!, "Saved")));
    }

    [Fact]
    public async Task Cancel_releases_confirmation_and_late_accept_cannot_start_a_run_or_replace_a_new_request()
    {
        using var temp = new TempDir();
        var firstConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = new MigrateViewModel(Settings(temp), autoDetect: false);
        var calls = 0;
        vm.Confirm = (_, _) => ++calls == 1 ? firstConfirmation.Task : secondConfirmation.Task;
        var first = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirming);
        Assert.True(vm.CanCancel);
        vm.CancelCommand.Execute(null);
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.IsConfirming);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanStart);

        var second = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirming);
        Assert.Equal(2, calls);
        firstConfirmation.SetResult(true);
        Assert.True(vm.IsConfirming);
        Assert.False(vm.IsBusy);
        secondConfirmation.SetResult(false);
        await second;
        Assert.False(vm.IsConfirming);
        Assert.False(vm.IsBusy);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(vm.TargetProjectPath)!, "Saved")));
    }
}
