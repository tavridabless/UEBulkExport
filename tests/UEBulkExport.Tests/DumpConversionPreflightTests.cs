namespace UEBulkExport.Tests;

public sealed class DumpConversionPreflightTests
{
    [Fact]
    public async Task Preflight_counts_source_and_reports_limitations_without_writing_or_recovering_files()
    {
        using var temp = new TempDir();
        var source = temp.Dir("Dump");
        System.IO.File.WriteAllBytes(Path.Combine(source, "Mesh.uasset"), [1, 2]);
        System.IO.File.WriteAllBytes(Path.Combine(source, "Mesh.uexp"), [3]);
        System.IO.File.WriteAllBytes(Path.Combine(source, "Mesh.ubulk"), []);
        System.IO.File.WriteAllBytes(Path.Combine(source, "Level.umap"), [4]);
        System.IO.File.WriteAllBytes(Path.Combine(source, "Texture.png"), [5]);
        System.IO.File.WriteAllBytes(Path.Combine(source, "Animation.psa"), [6]);
        System.IO.File.WriteAllBytes(Path.Combine(source, "Old.uasset.uebskip"), [7]);
        var project = temp.File("Target", "Target.uproject");
        System.IO.File.WriteAllText(project, """
            {"EngineAssociation":"5.5","Plugins":[{"Name":"PythonScriptPlugin","Enabled":true}]}
            """);
        var editor = temp.File("Tools", "UnrealEditor-Cmd.exe");
        var umodel = temp.File("Tools", "umodel.exe");
        var working = Path.Combine(temp.Path, "NotCreated");
        var before = Snapshot(temp.Path);
        var options = new DumpConversionOptions(source, "4.27", umodel, project, editor, "/Game/Plan", working);

        var plan = await new DumpConversionService().PreflightAsync(options);

        Assert.Equal(1, plan.CookedPackages);
        Assert.Equal(1, plan.MapPackages);
        Assert.Equal(2, plan.PayloadFiles);
        Assert.Equal(1, plan.EmptyPayloadFiles);
        Assert.Equal(1, plan.ImportableFiles);
        Assert.Equal(1, plan.ActorXFiles);
        Assert.Equal(1, plan.HiddenPackages);
        Assert.Equal(7, plan.SourceBytes);
        Assert.Equal("5.5", plan.TargetVersion);
        Assert.Equal(working, plan.WorkingDirectory);
        Assert.Contains(plan.Diagnostics, item => item.Code == "EmptyPayloads");
        Assert.Contains(plan.Diagnostics, item => item.Code == "ActorXUnsupported");
        Assert.Contains(plan.Diagnostics, item => item.Code == "HiddenPackages");
        Assert.Contains(plan.Diagnostics, item => item.Code == "CookedLimitations");
        Assert.Contains(plan.Diagnostics, item => item.Code == "VersionCompatibilityUnverified");
        Assert.DoesNotContain(plan.Diagnostics, item => item.Code == "PythonUnconfirmed" || item.Code == "PythonDisabled");
        Assert.Equal(before, Snapshot(temp.Path));
        Assert.False(Directory.Exists(working));
        Assert.False(System.IO.File.Exists(Path.Combine(source, "Old.uasset")));
    }

    [Theory]
    [InlineData("{\"EngineAssociation\":\"6.0\",\"Plugins\":[{\"Name\":\"PythonScriptPlugin\",\"Enabled\":false}]}", "PythonDisabled")]
    [InlineData("{\"EngineAssociation\":\"5.8\"}", "PythonUnconfirmed")]
    [InlineData("[]", "ProjectInvalid")]
    [InlineData("{broken", "ProjectUnreadable")]
    public async Task Descriptor_checks_do_not_claim_engine_compatibility_or_start_the_editor(string descriptor, string expected)
    {
        using var temp = new TempDir();
        var source = temp.Dir("Dump");
        temp.File("Dump", "Texture.png");
        var project = temp.File("Target", "Target.uproject");
        System.IO.File.WriteAllText(project, descriptor);
        var editor = temp.File("Tools", "UnrealEditor-Cmd.exe");
        var umodel = temp.File("Tools", "umodel.exe");
        var plan = await new DumpConversionService().PreflightAsync(new(source, "4.27", umodel,
            project, editor, "/Game/Checked"));
        Assert.Contains(plan.Diagnostics, item => item.Code == expected);
        Assert.Contains(plan.Diagnostics, item => item.Code == "VersionCompatibilityUnverified");
        if (expected is "PythonDisabled" or "ProjectInvalid" or "ProjectUnreadable") Assert.False(plan.CanProceed);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(project)!, "Saved")));
    }

    [Fact]
    public async Task Missing_inputs_and_maps_only_source_are_explicit_blockers()
    {
        using var temp = new TempDir();
        var source = temp.Dir("Dump");
        temp.File("Dump", "Only.umap");
        var plan = await new DumpConversionService().PreflightAsync(new(source, "4.27", "missing-umodel.exe",
            Path.Combine(temp.Path, "Missing.uproject"), "missing-editor.exe", "/Gameplay/WrongRoot"));
        Assert.False(plan.CanProceed);
        Assert.Equal(1, plan.MapPackages);
        foreach (var code in new[] { "NoImportableSource", "ProjectMissing", "EditorMissing", "UModelMissing", "DestinationInvalid" })
            Assert.Contains(plan.Diagnostics, item => item.Code == code && item.Level == DumpConversionDiagnosticLevel.Error);
    }

    [Fact]
    public async Task Pre_cancelled_plan_does_not_inspect_or_create_any_path()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DumpConversionService().PreflightAsync(
            new("missing-source", "not-a-version", "", "missing-project", "", "/not-game"), cancellation.Token));
    }

    [Fact]
    public async Task Hidden_packages_are_reported_as_recoverable_without_false_empty_source_blocker()
    {
        using var temp = new TempDir();
        var source = temp.Dir("Dump");
        var hidden = temp.File("Dump", "Mesh.uasset.uebskip");
        var project = temp.File("Target.uproject");
        System.IO.File.WriteAllText(project, "{}");
        var executable = temp.File("editor.exe");
        var plan = await new DumpConversionService().PreflightAsync(new(source, "4.27", executable,
            project, executable, "/Game/Recovery"));
        Assert.Equal(0, plan.CookedPackages);
        Assert.Equal(1, plan.HiddenPackages);
        Assert.DoesNotContain(plan.Diagnostics, item => item.Code == "NoImportableSource");
        Assert.Contains(plan.Diagnostics, item => item.Code == "HiddenPackages");
        Assert.True(System.IO.File.Exists(hidden));
        Assert.False(System.IO.File.Exists(Path.Combine(source, "Mesh.uasset")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"EngineAssociation\":false}")]
    [InlineData("{\"EngineAssociation\":{\"Version\":\"5.5\"}}")]
    public void Target_version_detection_handles_non_string_descriptors_without_getter_exceptions(string descriptor)
    {
        using var temp = new TempDir();
        var project = temp.File("Target.uproject");
        System.IO.File.WriteAllText(project, descriptor);
        Assert.Equal("5.5", DumpConversionService.DetectTargetVersion(project, Path.Combine(temp.Path, "UE_5.5", "UnrealEditor-Cmd.exe")));
    }

    [Theory]
    [InlineData("/Gameplay")]
    [InlineData("/GamePlugin/Assets")]
    public void Destination_must_match_the_Game_root_boundary(string destination) =>
        Assert.Throws<UserFacingException>(() => DumpConversionService.NormalizeDestination(destination));

    private static string[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + (System.IO.File.Exists(path)
            ? ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(path))) +
              ":" + System.IO.File.GetLastWriteTimeUtc(path).Ticks
            : ":directory"))
        .ToArray();
}
