using System.Text.Json;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

namespace UEBulkExport.Tests;

public sealed class BlueprintRecoveryTests
{
    private static readonly BlueprintRecoveryParameter Return = new("ReturnValue", "IntProperty", "Parm, ReturnParm",
        1, BlueprintParameterDirection.Return, "ChildProperties");
    private static readonly BlueprintRecoveryParameter Input = new("Value", "IntProperty", "Parm", 1,
        BlueprintParameterDirection.Input, "ChildProperties");

    [Fact]
    public void Bytecode_status_distinguishes_absent_native_and_incomplete_decoder_output()
    {
        var scan = new BlueprintRecoveryScan(new BlueprintRecoveryLimits(), default);
        Assert.Equal(BlueprintBytecodeStatus.Absent, scan.ReadInstructions(new UFunction()).Status);
        Assert.Equal(BlueprintBytecodeStatus.Native, scan.ReadInstructions(new UFunction
        {
            FunctionFlags = EFunctionFlags.FUNC_Native
        }).Status);
        Assert.Equal(BlueprintBytecodeStatus.Unparsed, scan.ReadInstructions(new UFunction { ScriptBytecode = [] }).Status);
        var prefix = scan.ReadInstructions(new UFunction { ScriptBytecode = [new EX_IntOne()] });
        Assert.Equal(BlueprintBytecodeStatus.Unparsed, prefix.Status);
        Assert.Equal(1, Assert.Single(prefix.Instructions).Value);
    }

    [Fact]
    public void Instruction_budget_counts_nested_operands_globally()
    {
        using var opcode = new FKismetArchive("TestFunction", [(byte)EExprToken.EX_IntOne], null!, new VersionContainer(EGame.GAME_UE5_8));
        var function = new UFunction { ScriptBytecode = [new EX_Return(opcode), new EX_EndOfScript()] };
        var scan = new BlueprintRecoveryScan(new BlueprintRecoveryLimits(MaxInstructions: 2), default);
        var result = scan.ReadInstructions(function);

        Assert.Equal(BlueprintBytecodeStatus.Truncated, result.Status);
        var returned = Assert.Single(result.Instructions);
        Assert.Equal(nameof(EExprToken.EX_Return), returned.Opcode);
        Assert.Equal(1, Assert.Single(returned.Operands).Value);
        Assert.Equal(BlueprintBytecodeStatus.Truncated, scan.ReadInstructions(function).Status);
        var report = scan.GetReport(1);
        Assert.True(report.InstructionLimitReached);
        Assert.Equal(2, report.InstructionsReported);
    }

    [Fact]
    public void Flow_distinguishes_branch_edges_unresolved_offsets_and_dynamic_targets()
    {
        var edges = BlueprintRecoveryScan.BuildFlow([
            Instruction(0, "EX_JumpIfNot", target: 50),
            Instruction(10, "EX_ComputedJump"),
            Instruction(20, "EX_Return"),
            Instruction(30, "EX_EndOfScript")
        ]);

        Assert.Equal([
            new BlueprintRecoveryEdge(0, 50, "when-false", false),
            new BlueprintRecoveryEdge(0, 10, "when-true", true),
            new BlueprintRecoveryEdge(10, null, "dynamic", false)
        ], edges);
    }

    [Fact]
    public void Constant_return_requires_a_complete_matching_cooked_body()
    {
        var body = AssignmentReturn(Instruction(18, "EX_IntConst", value: 42));
        var function = Function("GetAnswer", [Return], body);
        Assert.Equal(42, function.ConstantReturn);
        Assert.Null(function.ParameterReturn);
        Assert.Null((function with { BytecodeStatus = BlueprintBytecodeStatus.Truncated }).ConstantReturn);
        Assert.Null((function with { Instructions = [Instruction(0, "EX_FinalFunction"), .. body] }).ConstantReturn);
        Assert.Null((function with { Instructions = AssignmentReturn(Instruction(18, "EX_IntConst", value: 42), "Other") }).ConstantReturn);
        Assert.Null((function with { Parameters = [Return with { Type = "BoolProperty" }] }).ConstantReturn);
    }

    [Fact]
    public void Parameter_return_requires_the_exact_matching_local_input_and_same_type()
    {
        var function = Function("Identity", [Input, Return], AssignmentReturn(Instruction(18, "EX_LocalVariable", reference: "Value")));
        Assert.Equal("Value", function.ParameterReturn);
        Assert.Null(function.ConstantReturn);
        Assert.Null((function with { Parameters = [Input with { Type = "BoolProperty" }, Return] }).ParameterReturn);
        Assert.Null((function with { Instructions = AssignmentReturn(Instruction(18, "EX_InstanceVariable", reference: "Value")) }).ParameterReturn);
        Assert.Null((function with { Parameters = [Input with { Name = "Other" }, Return] }).ParameterReturn);
    }

    [Fact]
    public void Conditional_return_requires_two_typed_literal_assignments_and_the_exact_branch_targets()
    {
        var condition = Input with { Name = "UseHigher", Type = "BoolProperty" };
        var body = new[]
        {
            Instruction(0, "EX_JumpIfNot", operands: [Instruction(5, "EX_LocalVariable", reference: "UseHigher")], target: 42),
            Instruction(14, "EX_Let", operands: [Instruction(23, "EX_LocalOutVariable", reference: "ReturnValue"), Instruction(32, "EX_IntConst", value: 42)]),
            Instruction(37, "EX_Jump", target: 65),
            Instruction(42, "EX_Let", operands: [Instruction(51, "EX_LocalOutVariable", reference: "ReturnValue"), Instruction(60, "EX_IntConst", value: 1)]),
            Instruction(65, "EX_Return", operands: [Instruction(66, "EX_LocalOutVariable", reference: "ReturnValue")]),
            Instruction(75, "EX_EndOfScript")
        };
        var function = Function("Choose", [condition, Return], body);

        var branch = BlueprintRecovery.GetConditionalReturn(function);
        Assert.NotNull(branch);
        Assert.Equal("UseHigher", branch.Value.Condition);
        Assert.Equal(42, branch.Value.WhenTrue);
        Assert.Equal(1, branch.Value.WhenFalse);
        Assert.Null(BlueprintRecovery.GetConditionalReturn(function with { Pure = true }));
        Assert.Null(BlueprintRecovery.GetConditionalReturn(function with { Parameters = [condition with { Type = "IntProperty" }, Return] }));
        Assert.Null(BlueprintRecovery.GetConditionalReturn(function with { Instructions = [.. body.Take(5), Instruction(72, "EX_FinalFunction"), body[5]] }));
        Assert.Null(BlueprintRecovery.GetConditionalReturn(function with { Instructions = [body[0] with { TargetOffset = 14 }, .. body.Skip(1)] }));
        Assert.Null(BlueprintRecovery.GetConditionalReturn(function with { Instructions = [.. body.Take(2), body[2] with { TargetOffset = 75 }, .. body.Skip(3)] }));
    }

    [Fact]
    public void Cross_class_return_requires_resolved_member_actual_library_signature_and_matching_context()
    {
        const string owner = "/Game/Test.BFL_Target_C";
        const string member = owner + ":ReadValue";
        var worldContext = new BlueprintRecoveryParameter("__WorldContext", "ObjectProperty", "Parm", 1,
            BlueprintParameterDirection.Input, "ChildProperties") { TypeReference = "/Script/CoreUObject.Object" };
        var libraryFunction = Function("ReadValue", [Input, worldContext, Return],
            AssignmentReturn(Instruction(18, "EX_LocalVariable", reference: "Value"))) with
        {
            Path = member, Flags = "FUNC_Static, FUNC_BlueprintPure, FUNC_BlueprintCallable, FUNC_Public", Pure = true
        };
        const string cdo = "/Game/Test.Default__BFL_Target_C";
        var library = new BlueprintRecoveryClass("Game/Test.uasset", "BFL_Target_C", owner, BlueprintRecovery.FunctionLibraryParent,
            [], [libraryFunction], cdo, true);
        var receiver = Instruction(19, "EX_ObjectConst", reference: cdo) with
        {
            ReferenceKind = BlueprintRecoveryScan.BlueprintDefaultObjectImport, ReferenceOwner = owner
        };
        var call = Instruction(40, "EX_LocalVirtualFunction", reference: "ReadValue", operands:
            [Instruction(53, "EX_LocalVariable", reference: "Value"), Instruction(62, "EX_Self")]);
        var context = Instruction(18, "EX_Context", operands: [receiver, call], target: 64);
        var callerFunction = Function("CallTarget", [Input, Return], CallAndReturn(context)) with
        {
            LocalVariables = [new BlueprintRecoveryLocal("CallResult", "IntProperty", 1)]
        };
        var caller = new BlueprintRecoveryClass("Game/Source.uasset", "BP_Caller_C", "/Game/Source.BP_Caller_C",
            BlueprintRecovery.ActorParent, [], [callerFunction], null, true);
        var lookup = new Dictionary<string, BlueprintRecoveryClass> { [owner] = library };

        var body = Assert.Single(BlueprintRecovery.CreateReconstruction(caller, lookup).Functions).Body;
        Assert.Equal("function-call-return", body.Kind);
        Assert.Equal(owner, body.TargetClass);
        Assert.Equal("ReadValue", body.TargetFunction);
        Assert.Equal("Value", Assert.Single(body.Arguments!).Parameter);
        Assert.Equal("parameter-return", Assert.Single(BlueprintRecovery.CreateReconstruction(library).Functions).Body.Kind);
        Assert.Equal("stub", Assert.Single(BlueprintRecovery.CreateReconstruction(caller).Functions).Body.Kind);
        var linked = BlueprintRecovery.CreateLinkedReconstruction([caller, library], caller.Path);
        Assert.Equal([library.Path, caller.Path], linked.Blueprints.Select(plan => plan.SourceAsset));

        BlueprintReconstructionBody PlanWith(BlueprintRecoveryInstruction instruction)
        {
            var changed = callerFunction with { Instructions = CallAndReturn(instruction) };
            return Assert.Single(BlueprintRecovery.CreateReconstruction(caller with { Functions = [changed] }, lookup).Functions).Body;
        }
        Assert.Equal("stub", PlanWith(context with { Operands = [receiver with { ReferenceKind = null }, call] }).Kind);
        Assert.Equal("stub", PlanWith(context with { Operands = [receiver with { Reference = "/Game/Other.Default__SameName_C" }, call] }).Kind);
        Assert.Equal("stub", PlanWith(context with { Operands = [receiver, call with { Reference = "OtherFunction" }] }).Kind);
        Assert.Equal("stub", PlanWith(context with { Operands = [receiver, call with { Operands = [call.Operands[0], Instruction(62, "EX_NoObject")] }] }).Kind);
        Assert.Equal("stub", PlanWith(context with { TargetOffset = 91 }).Kind);
        Assert.Equal("stub", Assert.Single(BlueprintRecovery.CreateReconstruction(
            caller with { Functions = [callerFunction with { LocalVariables = [] }] }, lookup).Functions).Body.Kind);

        static BlueprintRecoveryInstruction[] CallAndReturn(BlueprintRecoveryInstruction expression) => [
            Instruction(0, "EX_Let", operands: [Instruction(9, "EX_LocalVariable", reference: "CallResult"), expression]),
            Instruction(64, "EX_Let", operands: [Instruction(73, "EX_LocalOutVariable", reference: "ReturnValue"),
                Instruction(82, "EX_LocalVariable", reference: "CallResult")]),
            Instruction(91, "EX_Return", operands: [Instruction(92, "EX_LocalOutVariable", reference: "ReturnValue")]),
            Instruction(101, "EX_EndOfScript")
        ];
    }

    [Fact]
    public void Plan_carries_proven_behaviour_and_explicit_stubs_and_omissions()
    {
        var source = new BlueprintRecoveryClass("Game/Test.uasset", "BP_Test_C", "/Game/Test.BP_Test_C",
            "/Script/Engine.Actor", [
                new BlueprintRecoveryVariable("Count", "IntProperty", "", 1, 7, BlueprintDefaultStatus.Serialized, "ChildProperties"),
                new BlueprintRecoveryVariable("Missing", "BoolProperty", "", 1, null, BlueprintDefaultStatus.NotSerialized, "ChildProperties")
            ], [
                Function("GetAnswer", [Return], AssignmentReturn(Instruction(18, "EX_IntConst", value: 42))),
                Function("Identity", [Input, Return], AssignmentReturn(Instruction(18, "EX_LocalVariable", reference: "Value"))),
                Function("Choose", [Input, Return], [Instruction(0, "EX_JumpIfNot"), Instruction(5, "EX_EndOfScript")])
            ], null, true);
        using var temp = new TempDir();
        var path = temp.File("Blueprint", "plan.json");

        BlueprintRecovery.WriteReconstruction(source, path);

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var variables = root.GetProperty("variables");
        Assert.Equal(7, variables[0].GetProperty("defaultValue").GetInt32());
        Assert.False(variables[1].TryGetProperty("defaultValue", out _));
        var functions = root.GetProperty("functions");
        Assert.Equal(42, functions[0].GetProperty("body").GetProperty("value").GetInt32());
        Assert.Equal("parameter-return", functions[1].GetProperty("body").GetProperty("kind").GetString());
        Assert.Equal("Value", functions[1].GetProperty("body").GetProperty("parameter").GetString());
        Assert.Equal("stub", functions[2].GetProperty("body").GetProperty("kind").GetString());
        Assert.NotEmpty(functions[2].GetProperty("body").GetProperty("reason").GetString()!);
        Assert.Single(root.GetProperty("omissions").EnumerateArray());
    }

    [Fact]
    public void Invalid_scan_limits_and_cancellation_are_checked_before_mounting()
    {
        var options = new Options();
        Assert.Throws<UserFacingException>(() => BlueprintRecovery.Inspect(options, new BlueprintRecoveryLimits(MaxPackages: 0)));
        Assert.Throws<UserFacingException>(() => BlueprintRecovery.Inspect(options, new BlueprintRecoveryLimits(MaxInstructions: int.MaxValue)));
        Assert.Throws<OperationCanceledException>(() => BlueprintRecovery.Inspect(options, cancellationToken: new CancellationToken(true)));
    }

    private static BlueprintRecoveryFunction Function(string name, BlueprintRecoveryParameter[] parameters,
        BlueprintRecoveryInstruction[] instructions) => new(name, $"/Game/Test.BP_Test_C:{name}", "FUNC_Public",
        BlueprintBytecodeStatus.Decoded, parameters, instructions, [], null, false, true);

    private static BlueprintRecoveryInstruction[] AssignmentReturn(BlueprintRecoveryInstruction value, string output = "ReturnValue") => [
        Instruction(0, "EX_Let", operands: [Instruction(9, "EX_LocalOutVariable", reference: output), value]),
        Instruction(23, "EX_Return", operands: [Instruction(24, "EX_LocalOutVariable", reference: output)]),
        Instruction(33, "EX_EndOfScript")
    ];

    private static BlueprintRecoveryInstruction Instruction(int offset, string opcode,
        BlueprintRecoveryInstruction[]? operands = null, string? reference = null, object? value = null, uint? target = null) =>
        new(offset, opcode, operands ?? [], reference, value, target);
}
