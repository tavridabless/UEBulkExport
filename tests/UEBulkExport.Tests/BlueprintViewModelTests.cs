using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Tests;

public sealed class BlueprintViewModelTests
{
    private static readonly GameOption DefaultProfile = new(EGame.GAME_UE5_3, "Unreal Engine 5.3");
    private static readonly GameOption OtherProfile = new(EGame.GAME_UE4_27, "Unreal Engine 4.27");

    [Fact]
    public void Export_source_handoff_copies_profile_keys_and_one_exact_package_header()
    {
        var source = new Options
        {
            PaksDirectory = "Source",
            Game = OtherProfile.Value,
            Platform = ETexturePlatform.Playstation5,
            AesKeys = ["key-one", "key-two"],
            UsmapPath = "fixture.usmap",
            OodlePath = "oodle-data-shared.dll",
            SelectedPaths = new HashSet<string> { "Game/Blueprints/BP_Example.uasset", "Game/Blueprints/BP_Example.uexp" }
        };
        using var model = new BlueprintViewModel([DefaultProfile, OtherProfile], () => source);

        model.UseExportSource();
        var options = model.BuildOptions();

        Assert.Equal("Source", options.PaksDirectory);
        Assert.Equal(OtherProfile.Value, options.Game);
        Assert.Equal(ETexturePlatform.Playstation5, options.Platform);
        Assert.Equal(["key-one", "key-two"], options.AesKeys);
        Assert.Equal("fixture.usmap", options.UsmapPath);
        Assert.Equal("oodle-data-shared.dll", options.OodlePath);
        Assert.True(options.SelectedPaths!.Contains("game/blueprints/bp_example.uasset"));
        Assert.Single(options.SelectedPaths);
    }

    [Fact]
    public void New_source_clears_a_previous_exact_package_target()
    {
        using var model = new BlueprintViewModel([DefaultProfile], () => new Options());
        model.PaksPath = "First";
        model.PackagePath = "Game\\Blueprints\\BP_Example.uasset";
        Assert.Equal("Game/Blueprints/BP_Example.uasset", Assert.Single(model.BuildOptions().SelectedPaths!));

        model.PaksPath = "Second";

        Assert.Empty(model.PackagePath);
        Assert.Null(model.BuildOptions().SelectedPaths);
    }

    [Fact]
    public void Exact_target_rejects_payloads_that_are_not_package_headers()
    {
        using var model = new BlueprintViewModel([DefaultProfile], () => new Options());
        model.PaksPath = "Source";
        model.PackagePath = "Game/Blueprints/BP_Example.uexp";

        Assert.Throws<UserFacingException>(() => model.BuildOptions());
    }

    [Fact]
    public void Indirect_links_follow_inspected_classes_and_handle_a_cycle()
    {
        var edges = new BlueprintDependencyEdge[]
        {
            new("Root", "Parent", BlueprintDependencyKind.Parent, BlueprintDependencyResolution.Inspected,
                null, null, null, true),
            new("Root", "Unknown", BlueprintDependencyKind.Call, BlueprintDependencyResolution.Unknown,
                "Run", null, 8, false),
            new("Parent", "Root", BlueprintDependencyKind.Type, BlueprintDependencyResolution.Inspected,
                null, "Child", null, false),
            new("Parent", "External", BlueprintDependencyKind.Type, BlueprintDependencyResolution.OutsideReport,
                null, "Field", null, false),
            new("Unknown", "DoNotFollow", BlueprintDependencyKind.Call, BlueprintDependencyResolution.Inspected,
                null, null, null, false)
        };
        var index = edges.ToLookup(edge => edge.Source, StringComparer.OrdinalIgnoreCase);

        var direct = BlueprintViewModel.CollectDependencyEdges(index, "root", includeTransitive: false);
        var indirect = BlueprintViewModel.CollectDependencyEdges(index, "root", includeTransitive: true);

        Assert.Equal(edges.Take(2), direct);
        Assert.Equal(edges.Take(4), indirect);
    }

    [Fact]
    public void Dependency_origin_keeps_class_and_function_and_localizes_missing_targets()
    {
        var edge = new BlueprintDependencyEdge("Game/Root.Root_C", "<unresolved>", BlueprintDependencyKind.Call,
            BlueprintDependencyResolution.Unknown, "Run", "ReadValue", 0x28);
        var row = new BlueprintDependencyRow(edge);

        Assert.Equal("Root_C · Run · ReadValue · 0x0028", row.Context);
        Assert.Equal("Game/Root.Root_C · Run · ReadValue · 0x0028", row.FullContext);
        Assert.Equal("<unresolved>", row.Target);
        Assert.NotEqual(row.Target, row.TargetLabel);
    }

    [Fact]
    public async Task Batch_plan_uses_the_entire_current_scan_and_reports_coverage()
    {
        using var directory = new TempDir();
        var output = Path.Combine(directory.Path, "plan.json");
        var first = ScannedClass("BP_First", BlueprintRecovery.ActorParent);
        var second = ScannedClass("BFL_Second", BlueprintRecovery.FunctionLibraryParent);
        var skipped = ScannedClass("BP_Widget", "/Script/UMG.UserWidget");
        var report = new BlueprintRecoveryReport(3, 3, 0, 0, 0, false, false, false, [first, second, skipped], [], 0);
        using var model = new BlueprintViewModel([DefaultProfile], () => new Options(), (_, _) => report)
        {
            PaksPath = "Source",
            PickBatchReconstructionPathAsync = () => Task.FromResult<string?>(output)
        };

        Assert.False(model.SaveBatchReconstructionCommand.CanExecute(null));
        await model.AnalyzeCommand.ExecuteAsync(null);
        model.SelectedClass = model.Classes.Single(item => item.Source == skipped);
        Assert.True(model.SaveBatchReconstructionCommand.CanExecute(null));
        await model.SaveBatchReconstructionCommand.ExecuteAsync(null);

        using var plan = JsonDocument.Parse(File.ReadAllText(output));
        var paths = plan.RootElement.GetProperty("blueprints").EnumerateArray()
            .Select(item => item.GetProperty("sourceAsset").GetString()).ToHashSet();
        Assert.Equal(2, paths.Count);
        Assert.Contains(first.Path, paths);
        Assert.Contains(second.Path, paths);
        Assert.Equal(Loc.Instance.Format("Blueprint.Batch.Saved", 2, 3, 1, 0, 0, 0, 0), model.StatusText);
        Assert.Equal(skipped.Path, model.SelectedClass?.Path);
        model.PaksPath = "New Source";
        Assert.False(model.SaveBatchReconstructionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Batch_with_no_eligible_class_saves_coverage_without_claiming_restoration()
    {
        using var directory = new TempDir();
        var output = Path.Combine(directory.Path, "coverage.json");
        var skipped = ScannedClass("BP_Widget", "/Script/UMG.UserWidget");
        var report = new BlueprintRecoveryReport(1, 1, 0, 0, 0, false, false, false, [skipped], [], 0);
        using var model = new BlueprintViewModel([DefaultProfile], () => new Options(), (_, _) => report)
        {
            PaksPath = "Source",
            PickBatchReconstructionPathAsync = () => Task.FromResult<string?>(output)
        };

        await model.AnalyzeCommand.ExecuteAsync(null);
        await model.SaveBatchReconstructionCommand.ExecuteAsync(null);

        using var plan = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(0, plan.RootElement.GetProperty("blueprints").GetArrayLength());
        Assert.Equal(Loc.Instance.Format("Blueprint.Batch.CoverageOnly", 1), model.StatusText);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Project_recovery_uses_the_scan_and_shows_the_partial_project_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        var report = new BlueprintRecoveryReport(1, 1, 0, 0, 0, false, false, false,
            [ScannedClass("BP_First", BlueprintRecovery.ActorParent)], [], 0);
        var projectPath = Path.Combine("Chosen", "Recovery", "Project", "RecoveredProject.uproject");
        var result = new BlueprintProjectRecoveryResult("Recovery", projectPath,
            new BlueprintBatchReconstructionResult(1, 1, 0, 0, 1, 0, 0, false), true);
        (BlueprintRecoveryReport Report, Options Source, string Editor, string Output)? received = null;
        using var model = new BlueprintViewModel([DefaultProfile], () => new Options(), (_, _) => report)
        {
            PaksPath = "Source",
            PickProjectRecoveryPathsAsync = () => Task.FromResult<(string, string)?>(("UnrealEditor-Cmd.exe", "Chosen")),
            RecoverProjectAsync = (scan, source, editor, output, _) =>
            {
                received = (scan, source, editor, output);
                return Task.FromResult(result);
            }
        };

        Assert.False(model.RecoverProjectCommand.CanExecute(null));
        await model.AnalyzeCommand.ExecuteAsync(null);
        Assert.True(model.RecoverProjectCommand.CanExecute(null));
        await model.RecoverProjectCommand.ExecuteAsync(null);

        Assert.Same(report, received?.Report);
        Assert.Equal("Source", received?.Source.PaksDirectory);
        Assert.Equal("UnrealEditor-Cmd.exe", received?.Editor);
        Assert.Equal("Chosen", received?.Output);
        Assert.Equal(Loc.Instance.Format("Blueprint.ProjectRecovery.Partial", projectPath), model.StatusText);
        Assert.False(model.IsBusy);
        Assert.True(model.HasReport);
    }

    [Fact]
    public async Task Cancelling_project_recovery_forwards_cancellation_and_keeps_the_scan()
    {
        if (!OperatingSystem.IsWindows()) return;
        var report = new BlueprintRecoveryReport(1, 1, 0, 0, 0, false, false, false,
            [ScannedClass("BP_First", BlueprintRecovery.ActorParent)], [], 0);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = new BlueprintViewModel([DefaultProfile], () => new Options(), (_, _) => report)
        {
            PaksPath = "Source",
            PickProjectRecoveryPathsAsync = () => Task.FromResult<(string, string)?>(("UnrealEditor-Cmd.exe", "Chosen")),
            RecoverProjectAsync = async (_, _, _, _, cancellation) =>
            {
                started.SetResult(cancellation);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                throw new InvalidOperationException();
            }
        };

        await model.AnalyzeCommand.ExecuteAsync(null);
        var recovery = model.RecoverProjectCommand.ExecuteAsync(null);
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.IsBusy);
        Assert.True(model.CancelCommand.CanExecute(null));
        Assert.False(model.SaveBatchReconstructionCommand.CanExecute(null));
        model.CancelCommand.Execute(null);
        await recovery;

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(Loc.Instance["Blueprint.ProjectRecovery.Cancelled"], model.StatusText);
        Assert.False(model.IsBusy);
        Assert.True(model.HasReport);
    }

    private static BlueprintRecoveryClass ScannedClass(string name, string parent) =>
        new($"Game/{name}.uasset", $"{name}_C", $"Game/{name}.{name}_C", parent, [], [], null, true);
}
