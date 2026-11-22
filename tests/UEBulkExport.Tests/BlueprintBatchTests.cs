using System.Text.Json;

namespace UEBulkExport.Tests;

public sealed class BlueprintBatchTests
{
    private const string CallerPath = "/Game/AActor.BP_Caller_C";
    private const string LibraryPath = "/Game/ZLibrary.BFL_Target_C";
    private const string LibraryDefault = "/Game/ZLibrary.Default__BFL_Target_C";
    private static readonly BlueprintRecoveryParameter Input = new("Value", "IntProperty", "Parm", 1,
        BlueprintParameterDirection.Input, "ChildProperties");
    private static readonly BlueprintRecoveryParameter Return = new("ReturnValue", "IntProperty", "Parm, ReturnParm", 1,
        BlueprintParameterDirection.Return, "ChildProperties");

    [Fact]
    public void Batch_includes_unrelated_classes_orders_dependencies_and_reports_gaps()
    {
        var (caller, library) = LinkedClasses();
        var extraPath = "/Game/Independent.BP_Independent_C";
        var extra = Class(extraPath, "BP_Independent_C") with
        {
            Functions = [
                Function(extraPath, "Unavailable", []) with { BytecodeStatus = BlueprintBytecodeStatus.Absent, Diagnostic = "Cooked body unavailable." },
                Function(extraPath, "Unsupported", []) with { Flags = "FUNC_Native" }
            ]
        };
        var unsupported = Class("/Game/Child.BP_Child_C", "BP_Child_C") with { Parent = "/Script/Engine.Pawn" };
        var incomplete = Class("/Game/Partial.BP_Partial_C", "BP_Partial_C") with { MetadataComplete = false };
        var report = Report(caller, unsupported, library, extra, incomplete) with { LockedContainers = 1 };

        var plan = BlueprintRecovery.CreateBatchReconstruction(report);

        Assert.Equal([extraPath, LibraryPath, CallerPath], plan.Blueprints.Select(item => item.SourceAsset));
        var call = Assert.Single(plan.Blueprints[2].Functions).Body;
        Assert.Equal("function-call-return", call.Kind);
        Assert.Equal(LibraryPath, call.TargetClass);
        Assert.Equal("ReadValue", call.TargetFunction);
        var coverage = Assert.IsType<BlueprintBatchCoverage>(plan.Coverage);
        Assert.Equal(new BlueprintBatchReconstructionResult(5, 3, 2, 2, 1, 1, 1, true), coverage.Summary);
        Assert.True(coverage.Summary.HasGaps);
        Assert.Equal([BlueprintBatchExclusion.UnsupportedParent, BlueprintBatchExclusion.IncompleteMetadata],
            coverage.ExcludedClasses.Select(item => item.Reason));
        Assert.Equal(new BlueprintBatchStub(extraPath, "Unavailable", "Cooked body unavailable."), Assert.Single(coverage.Stubs));
    }

    [Fact]
    public void Blueprint_parent_chain_is_required_and_restored_before_descendants()
    {
        var ancestor = Class("/Game/ZBase.BP_Base_C", "BP_Base_C");
        var parent = Class("/Game/BParent.BP_Parent_C", "BP_Parent_C") with { Parent = ancestor.Path };
        var child = Class("/Game/AChild.BP_Child_C", "BP_Child_C") with { Parent = parent.Path };
        var report = Report(child, parent, ancestor);

        var batch = BlueprintRecovery.CreateBatchReconstruction(report);
        var linked = BlueprintRecovery.CreateLinkedReconstruction(report.Classes, child.Path);
        var map = BlueprintRecovery.BuildDependencyMap(report.Classes, default);

        Assert.Equal([ancestor.Path, parent.Path, child.Path], batch.Blueprints.Select(item => item.SourceAsset));
        Assert.Equal(batch.Blueprints.Select(item => item.SourceAsset), linked.Blueprints.Select(item => item.SourceAsset));
        Assert.Equal([BlueprintRecovery.ActorParent, ancestor.Path, parent.Path], linked.Blueprints.Select(item => item.ParentClass));
        Assert.Equal(2, map.Edges.Count(edge => edge.Kind == BlueprintDependencyKind.Parent && edge.IsRequiredForReconstruction));
        Assert.Throws<UserFacingException>(() => BlueprintRecovery.CreateReconstruction(child));
    }

    [Fact]
    public void Excluded_and_unresolved_parent_classes_do_not_leave_a_partial_inheritance_chain()
    {
        var parent = Class("/Game/Parent.BP_Base_C", "BP_Base_C");
        var collision = parent with { Path = "/Game/Other.BP_Base_C" };
        var child = Class("/Game/Child.BP_Child_C", "BP_Child_C") with { Parent = parent.Path };
        var nested = Class("/Game/Nested.BP_Nested_C", "BP_Nested_C") with { Parent = child.Path };
        var cyclicParent = parent with { Parent = nested.Path };

        var colliding = BlueprintRecovery.CreateBatchReconstruction(Report(nested, child, collision, parent));
        var cyclic = BlueprintRecovery.CreateBatchReconstruction(Report(nested, child, cyclicParent));

        Assert.Empty(colliding.Blueprints);
        Assert.Equal(2, colliding.Coverage!.ExcludedClasses.Count(item => item.Reason == BlueprintBatchExclusion.ParentExcluded));
        Assert.Empty(cyclic.Blueprints);
        Assert.All(cyclic.Coverage!.ExcludedClasses, item => Assert.Equal(BlueprintBatchExclusion.UnsupportedParent, item.Reason));
    }

    [Fact]
    public void Inheritance_plan_verifies_override_signature_and_restores_serialized_parent_field_default()
    {
        var parent = Class("/Game/Parent.BP_Parent_C", "BP_Parent_C");
        var child = Class("/Game/Child.BP_Child_C", "BP_Child_C") with { Parent = parent.Path };
        var inherited = Function(parent.Path, "ReadCount",
            [Op(0, "EX_Return", null, Op(1, "EX_IntOne", null) with { Value = 1 }), Op(2, "EX_EndOfScript", null)]) with
        {
            Parameters = [Return],
            Pure = true,
            Flags = "FUNC_Public, FUNC_BlueprintPure, FUNC_BlueprintCallable"
        };
        parent = parent with
        {
            Functions = [inherited],
            Variables = [
                new("Count", "IntProperty", "", 1, 1, BlueprintDefaultStatus.Serialized, "ChildProperties"),
                new("Ready", "BoolProperty", "Edit, BlueprintVisible", 1, null, BlueprintDefaultStatus.NotSerialized, "ChildProperties"),
                new("UberGraphFrame", "StructProperty", "Transient, DuplicateTransient", 1, null, BlueprintDefaultStatus.UnsupportedType,
                    "ChildProperties") { TypeReference = "/Script/Engine.PointerToUberGraphFrame" }
            ]
        };
        var implementation = inherited with
        {
            Path = $"{child.Path}:ReadCount", SuperFunction = inherited.Path,
            Instructions = [Op(0, "EX_Return", null, Op(1, "EX_IntConst", null) with { Value = 29 }), Op(6, "EX_EndOfScript", null)]
        };
        child = child with
        {
            Functions = [implementation],
            InheritedDefaults = [new("Count", "int", 13), new("UnknownNativeField", "bool", true)]
        };

        var group = BlueprintRecovery.CreateLinkedReconstruction([child, parent], child.Path).Blueprints;
        var plan = group[^1];

        Assert.False(Assert.IsType<bool>(group[0].Variables.Single(variable => variable.Name == "Ready").DefaultValue));
        Assert.DoesNotContain(group[0].Omissions, item => item.Contains("UberGraphFrame", StringComparison.Ordinal));
        var plannedOverride = Assert.Single(plan.Functions);
        Assert.Equal(parent.Path, plannedOverride.OverrideClass);
        Assert.Equal(29, plannedOverride.Body.Value);
        Assert.Equal(new BlueprintRecoveryInheritedDefault("Count", "int", 13), Assert.Single(plan.InheritedDefaults!));
        Assert.Contains(plan.Omissions, item => item.Contains("UnknownNativeField", StringComparison.Ordinal));

        var malformed = child with { Functions = [implementation with { SuperFunction = "/Game/Unrelated.Foo_C:ReadCount" }] };
        var rejected = BlueprintRecovery.CreateLinkedReconstruction([malformed, parent], malformed.Path).Blueprints[^1];
        Assert.Empty(rejected.Functions);
        Assert.Contains(rejected.Omissions, item => item.Contains("unsupported override", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_names_exclude_both_classes_and_make_the_linked_call_a_stub()
    {
        var (caller, library) = LinkedClasses();
        var collision = library with { Path = "/Game/Other.BFL_Target_C", Name = "bfl_target_C" };
        var plan = BlueprintRecovery.CreateBatchReconstruction(Report(caller, collision, library));
        var reversed = BlueprintRecovery.CreateBatchReconstruction(Report(library, collision, caller));

        Assert.Equal("stub", Assert.Single(Assert.Single(plan.Blueprints).Functions).Body.Kind);
        var excluded = plan.Coverage!.ExcludedClasses;
        Assert.Equal([collision.Path, library.Path], excluded.Select(item => item.ClassPath));
        Assert.All(excluded, item => Assert.Equal(BlueprintBatchExclusion.DuplicateDestinationName, item.Reason));
        Assert.Equal(excluded, reversed.Coverage!.ExcludedClasses);
    }

    [Fact]
    public void Batch_with_no_eligible_classes_writes_machine_readable_schema_two_coverage()
    {
        using var temp = new TempDir();
        var path = temp.File("batch.json");
        var duplicate = Class(CallerPath, "BP_Caller_C");
        var invalid = Class("/Game/Invalid.Bad-Name_C", "Bad-Name_C");

        var result = BlueprintRecovery.WriteBatchReconstruction(Report(duplicate, duplicate, invalid), path);

        Assert.Equal(0, result.ClassesIncluded);
        Assert.Equal(3, result.ClassesExcluded);
        Assert.True(result.HasGaps);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("blueprints").EnumerateArray());
        var exclusions = json.RootElement.GetProperty("coverage").GetProperty("excludedClasses").EnumerateArray();
        Assert.Equal(["AmbiguousSourcePath", "AmbiguousSourcePath", "UnrestorableClass"],
            exclusions.Select(item => item.GetProperty("reason").GetString()));

        var cancelled = temp.File("cancelled.json");
        File.Delete(cancelled);
        Assert.Throws<OperationCanceledException>(() => BlueprintRecovery.WriteBatchReconstruction(Report(invalid), cancelled,
            new CancellationToken(canceled: true)));
        Assert.False(File.Exists(cancelled));
    }

    [Fact]
    public void Batch_uses_the_scan_bounds_and_does_not_apply_the_single_root_link_limit()
    {
        var classes = Enumerable.Range(0, BlueprintRecovery.MaxLinkedBlueprints + 1)
            .Select(index => Class($"/Game/Samples/BP_{index:D3}.BP_{index:D3}_C", $"BP_{index:D3}_C"))
            .Reverse().ToArray();

        var plan = BlueprintRecovery.CreateBatchReconstruction(Report(classes));

        Assert.Equal(classes.Reverse().Select(item => item.Path), plan.Blueprints.Select(item => item.SourceAsset));
        Assert.False(plan.Coverage!.Summary.HasGaps);
    }

    [Fact]
    public void Batch_report_marks_unresolved_export_diagnostics_even_if_every_package_was_scanned()
    {
        var diagnostic = new BlueprintRecoveryDiagnostic("Game/Unknown.uasset", "/Game/Unknown.BP_Unknown_C",
            "Export class reference could not be resolved.");
        var report = Report(Class(CallerPath, "BP_Caller_C")) with { Diagnostics = [diagnostic] };

        var coverage = BlueprintRecovery.CreateBatchReconstruction(report).Coverage!;

        Assert.True(coverage.Summary.ScanIncomplete);
        Assert.True(coverage.Summary.HasGaps);
        Assert.Equal(diagnostic, Assert.Single(coverage.Scan.Diagnostics));
    }

    private static BlueprintRecoveryReport Report(params BlueprintRecoveryClass[] classes) =>
        new(classes.Length, classes.Length, 0, 0, 0, false, false, false, classes, [], 0);

    private static BlueprintRecoveryClass Class(string path, string name) =>
        new(path, name, path, BlueprintRecovery.ActorParent, [], [], null, true);

    private static BlueprintRecoveryFunction Function(string path, string name, BlueprintRecoveryInstruction[] instructions) =>
        new(name, $"{path}:{name}", "FUNC_Public", BlueprintBytecodeStatus.Decoded, [Input, Return], instructions, [], null, false, true);

    private static (BlueprintRecoveryClass Caller, BlueprintRecoveryClass Library) LinkedClasses()
    {
        var receiver = Op(19, "EX_ObjectConst", LibraryDefault) with
        {
            ReferenceOwner = LibraryPath, ReferenceKind = BlueprintRecoveryScan.BlueprintDefaultObjectImport
        };
        var context = Op(18, "EX_Context", null, receiver,
            Op(40, "EX_LocalVirtualFunction", "ReadValue", Op(53, "EX_LocalVariable", "Value"))) with { TargetOffset = 63 };
        var call = Function(CallerPath, "CallTarget", [
            Op(0, "EX_Let", null, Op(9, "EX_LocalVariable", "Temp"), context),
            Op(63, "EX_Let", null, Op(72, "EX_LocalOutVariable", "ReturnValue"), Op(81, "EX_LocalVariable", "Temp")),
            Op(90, "EX_Return", null, Op(91, "EX_LocalOutVariable", "ReturnValue")),
            Op(100, "EX_EndOfScript", null)
        ]) with { LocalVariables = [new BlueprintRecoveryLocal("Temp", "IntProperty", 1)] };
        var target = Function(LibraryPath, "ReadValue", [
            Op(0, "EX_Return", null, Op(1, "EX_LocalVariable", "Value")), Op(10, "EX_EndOfScript", null)
        ]) with { Pure = true, Flags = "FUNC_Static, FUNC_Public, FUNC_BlueprintPure, FUNC_BlueprintCallable" };
        return (Class(CallerPath, "BP_Caller_C") with { Functions = [call] },
            Class(LibraryPath, "BFL_Target_C") with
            {
                Parent = BlueprintRecovery.FunctionLibraryParent, DefaultObject = LibraryDefault, Functions = [target]
            });
    }

    private static BlueprintRecoveryInstruction Op(int offset, string opcode, string? reference,
        params BlueprintRecoveryInstruction[] operands) => new(offset, opcode, operands, reference, null, null);
}
