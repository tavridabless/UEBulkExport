namespace UEBulkExport.Tests;

public sealed class BlueprintStatefulTests
{
    private const string Owner = "/Game/AOwner.BP_Owner_C";
    private const string Target = "/Game/ZTarget.BP_Target_C";
    private static readonly BlueprintRecoveryParameter Value = new("Value", "IntProperty", "Parm", 1,
        BlueprintParameterDirection.Input, "ChildProperties");
    private static readonly BlueprintRecoveryParameter Return = new("ReturnValue", "IntProperty", "Parm, ReturnParm", 1,
        BlueprintParameterDirection.Return, "ChildProperties");

    [Fact]
    public void Scalar_read_and_write_require_the_same_declared_instance_field()
    {
        var good = Class(Owner, "BP_Owner_C", Read(Owner), Write(Owner));
        var forms = BlueprintRecovery.CreateReconstruction(good).Functions;
        Assert.Equal(["property-read", "property-write"], forms.Select(function => function.Body.Kind));
        Assert.Equal("Count", forms[0].Body.Property);
        Assert.Equal("Value", forms[1].Body.Parameter);

        var wrongOwner = good with { Functions = [Read(Owner, Target), Write(Owner, Target)] };
        Assert.All(BlueprintRecovery.CreateReconstruction(wrongOwner).Functions, function => Assert.Equal("stub", function.Body.Kind));

        var replicated = good with { Variables = [good.Variables[0] with { Flags = "Net, RepNotify" }] };
        Assert.All(BlueprintRecovery.CreateReconstruction(replicated).Functions, function => Assert.Equal("stub", function.Body.Kind));
    }

    [Fact]
    public void Event_needs_proven_local_dispatcher_and_complete_assignment_trampoline()
    {
        const string graph = "ExecuteUbergraph_BP_Owner";
        var graphPath = $"{Owner}:{graph}";
        var entryParameter = Value with { Name = "EntryPoint" };
        var dispatcher = Function(Owner, graph, "FUNC_Final, FUNC_UbergraphFunction", [entryParameter], [
            Op(0, "EX_ComputedJump", operands: [Op(1, "EX_LocalVariable", "EntryPoint") with { ReferenceOwner = graphPath }]),
            Op(10, "EX_Let", operands: [Field(19, Owner), Op(28, "EX_IntConst", value: 19)]),
            Op(33, "EX_Jump", target: 43), Op(38, "EX_Jump", target: 10), VoidReturn(43), Op(45, "EX_EndOfScript")
        ]);
        var eventBody = new[]
        {
            Op(0, "EX_LocalFinalFunction", graphPath, operands: [Op(9, "EX_IntConst", value: 38)]) with
            {
                ReferenceOwner = Owner, ReferenceName = graph, ReferenceKind = BlueprintRecoveryScan.BlueprintLocalFunction
            },
            VoidReturn(15), Op(17, "EX_EndOfScript")
        };
        var wrapper = Function(Owner, "ResetCount", "FUNC_BlueprintCallable, FUNC_BlueprintEvent", [], eventBody);
        var source = Class(Owner, "BP_Owner_C", wrapper, dispatcher);
        var plan = BlueprintRecovery.CreateBatchReconstruction(Report(source));
        var recreated = Assert.Single(Assert.Single(plan.Blueprints).Events!);
        Assert.Equal("ResetCount", recreated.Name);
        Assert.Equal("property-write", recreated.Body.Kind);
        Assert.Equal(19, recreated.Body.Value);
        Assert.Equal(1, plan.Coverage!.Summary.EventsReconstructed);
        Assert.Equal(0, plan.Coverage.Summary.FunctionsOmitted);

        var badTrampoline = dispatcher with { Instructions = [.. dispatcher.Instructions.Take(3), Op(38, "EX_Jump", target: 43), .. dispatcher.Instructions.Skip(4)] };
        var invalid = BlueprintRecovery.CreateReconstruction(source with { Functions = [wrapper, badTrampoline] });
        Assert.Null(invalid.Events);
        Assert.Empty(invalid.Functions);
        Assert.Contains(invalid.Omissions, text => text.StartsWith("Event ResetCount", StringComparison.Ordinal));
        var withParameter = BlueprintRecovery.CreateReconstruction(source with { Functions = [wrapper with { Parameters = [Value] }, dispatcher] });
        Assert.Null(withParameter.Events);
        Assert.Contains(withParameter.Omissions, text => text.Contains("event parameters", StringComparison.Ordinal));
    }

    [Fact]
    public void Multiple_events_use_exact_typed_frame_bindings_and_keep_uncovered_dispatcher_code_as_an_omission()
    {
        const string graph = "ExecuteUbergraph_BP_Owner";
        var graphPath = $"{Owner}:{graph}";
        var setCount = Wrapper("AssignCount", Value, "CountInput", 80);
        var setReady = Wrapper("AssignReady", Value with { Name = "ReadyValue", Type = "BoolProperty" }, "ReadyInput", 85);
        var ready = Field(49, Owner) with { Reference = "Ready" };
        var dispatcher = Function(Owner, graph, "FUNC_Final, FUNC_UbergraphFunction", [Value with { Name = "EntryPoint" }], [
            Op(0, "EX_ComputedJump", operands: [Op(1, "EX_LocalVariable", "EntryPoint") with { ReferenceOwner = graphPath }]),
            Op(10, "EX_Let", operands: [Field(19, Owner), Op(28, "EX_LocalVariable", "CountInput") with { ReferenceOwner = graphPath }]),
            Op(37, "EX_Jump", target: 90),
            Op(48, "EX_LetBool", operands: [ready, Op(58, "EX_LocalVariable", "ReadyInput") with { ReferenceOwner = graphPath }]),
            Op(67, "EX_Jump", target: 90),
            Op(80, "EX_Jump", target: 10), Op(85, "EX_Jump", target: 48), VoidReturn(90), Op(92, "EX_EndOfScript")
        ]) with
        {
            LocalVariables = [new BlueprintRecoveryLocal("CountInput", "IntProperty", 1), new BlueprintRecoveryLocal("ReadyInput", "BoolProperty", 1)]
        };
        var source = Class(Owner, "BP_Owner_C", setCount, setReady, dispatcher);
        source = source with { Variables = [.. source.Variables,
            new BlueprintRecoveryVariable("Ready", "BoolProperty", "BlueprintVisible", 1, false, BlueprintDefaultStatus.Serialized, "ChildProperties")] };

        var result = BlueprintRecovery.CreateBatchReconstruction(Report(source));
        var plan = Assert.Single(result.Blueprints);
        Assert.Collection(plan.Events!, first =>
        {
            Assert.Equal("AssignCount", first.Name);
            Assert.Equal(new BlueprintReconstructionPin("Value", "int"), Assert.Single(first.Inputs));
            Assert.Equal("Count", first.Body.Property);
            Assert.Equal("Value", first.Body.Parameter);
        }, second =>
        {
            Assert.Equal("AssignReady", second.Name);
            Assert.Equal(new BlueprintReconstructionPin("ReadyValue", "bool"), Assert.Single(second.Inputs));
            Assert.Equal("Ready", second.Body.Property);
            Assert.Equal("ReadyValue", second.Body.Parameter);
        });
        Assert.Empty(plan.Omissions);
        Assert.Equal(2, result.Coverage!.Summary.EventsReconstructed);
        Assert.Equal(0, result.Coverage.Summary.FunctionsOmitted);

        var foreignSlot = setReady with { Instructions = [setReady.Instructions[0] with { Reference = "CountInput" }, .. setReady.Instructions.Skip(1)] };
        var partial = BlueprintRecovery.CreateBatchReconstruction(Report(source with { Functions = [setCount, foreignSlot, dispatcher] }));
        var partialPlan = Assert.Single(partial.Blueprints);
        Assert.Equal("AssignCount", Assert.Single(partialPlan.Events!).Name);
        Assert.Contains(partialPlan.Omissions, item => item.StartsWith("Event AssignReady", StringComparison.Ordinal));
        Assert.Contains(partialPlan.Omissions, item => item.StartsWith($"Function {graph}", StringComparison.Ordinal));
        Assert.Equal(2, partial.Coverage!.Summary.FunctionsOmitted);

        BlueprintRecoveryFunction Wrapper(string name, BlueprintRecoveryParameter parameter, string slot, int offset) =>
            Function(Owner, name, "FUNC_BlueprintCallable, FUNC_BlueprintEvent", [parameter], [
                Op(0, "EX_LetValueOnPersistentFrame", slot, operands: [Op(9, "EX_LocalVariable", parameter.Name) with { ReferenceOwner = $"{Owner}:{name}" }]) with
                    { ReferenceOwner = graphPath },
                Op(18, "EX_LocalFinalFunction", graphPath, operands: [Op(27, "EX_IntConst", value: offset)]) with
                    { ReferenceOwner = Owner, ReferenceName = graph, ReferenceKind = BlueprintRecoveryScan.BlueprintLocalFunction },
                VoidReturn(33), Op(35, "EX_EndOfScript")
            ]);
    }

    [Fact]
    public void Instance_call_correlates_declared_receiver_frame_type_and_callee()
    {
        var target = Class(Target, "BP_Target_C", Read(Target));
        var other = Value with { Name = "Other", Type = "ObjectProperty", TypeReference = Target };
        var path = Owner + ":ReadOther";
        var receiver = Op(19, "EX_LocalVariable", "Other") with { ReferenceOwner = path };
        var call = Function(Owner, "ReadOther", "FUNC_Public", [other, Return], CallBody(receiver)) with
        {
            LocalVariables = [new BlueprintRecoveryLocal("Result", "IntProperty", 1)]
        };
        var source = Class(Owner, "BP_Owner_C", call);
        var plan = BlueprintRecovery.CreateBatchReconstruction(Report(source, target));

        Assert.Equal([Target, Owner], plan.Blueprints.Select(blueprint => blueprint.SourceAsset));
        var rebuilt = Assert.Single(plan.Blueprints[1].Functions);
        Assert.Equal(new BlueprintReconstructionPin("Other", "object", Target), Assert.Single(rebuilt.Inputs));
        Assert.Equal("instance-call-return", rebuilt.Body.Kind);
        Assert.Equal("GetCount", rebuilt.Body.TargetFunction);
        Assert.Equal("Other", rebuilt.Body.Receiver);
        var map = BlueprintRecovery.BuildDependencyMap([source, target], default);
        Assert.Contains(map.Edges, edge => edge.Source == Owner && edge.Target == Target && edge.Member == "GetCount" &&
            edge.Kind == BlueprintDependencyKind.Call && edge.IsRuntimeDispatch && edge.IsRequiredForReconstruction);
        Assert.Empty(Assert.Single(BlueprintRecovery.CreateBatchReconstruction(Report(source)).Blueprints).Functions);

        var wrongFrame = source with { Functions = [call with { Instructions = CallBody(receiver with { ReferenceOwner = null }) }] };
        var unsupported = BlueprintRecovery.CreateBatchReconstruction(Report(wrongFrame, target));
        Assert.Empty(unsupported.Blueprints.Single(blueprint => blueprint.SourceAsset == Owner).Functions);
    }

    private static BlueprintRecoveryInstruction[] CallBody(BlueprintRecoveryInstruction receiver) => [
        Op(0, "EX_Let", operands: [Op(9, "EX_LocalVariable", "Result"), Op(18, "EX_Context", target: 54,
            operands: [receiver, Op(40, "EX_LocalVirtualFunction", "GetCount")])]),
        Op(54, "EX_Let", operands: [Op(63, "EX_LocalOutVariable", "ReturnValue"), Op(72, "EX_LocalVariable", "Result")]),
        Op(81, "EX_Return", operands: [Op(82, "EX_LocalOutVariable", "ReturnValue")]), Op(91, "EX_EndOfScript")
    ];

    private static BlueprintRecoveryFunction Read(string path, string? propertyOwner = null) =>
        Function(path, "GetCount", "FUNC_Public, FUNC_BlueprintCallable, FUNC_BlueprintPure", [Return], [
            Op(0, "EX_Return", operands: [Field(1, propertyOwner ?? path)]), Op(10, "EX_EndOfScript")
        ]) with { Pure = true };

    private static BlueprintRecoveryFunction Write(string path, string? propertyOwner = null) =>
        Function(path, "SetCount", "FUNC_Public", [Value], [
            Op(0, "EX_Let", operands: [Field(9, propertyOwner ?? path), Op(18, "EX_LocalVariable", "Value")]),
            VoidReturn(27), Op(29, "EX_EndOfScript")
        ]);

    private static BlueprintRecoveryClass Class(string path, string name, params BlueprintRecoveryFunction[] functions) =>
        new(path, name, path, BlueprintRecovery.ActorParent,
            [new BlueprintRecoveryVariable("Count", "IntProperty", "BlueprintVisible", 1, 3, BlueprintDefaultStatus.Serialized, "ChildProperties")],
            functions, null, true);

    private static BlueprintRecoveryReport Report(params BlueprintRecoveryClass[] classes) =>
        new(classes.Length, classes.Length, 0, 0, 0, false, false, false, classes, [], 0);

    private static BlueprintRecoveryFunction Function(string path, string name, string flags, BlueprintRecoveryParameter[] parameters,
        BlueprintRecoveryInstruction[] instructions) => new(name, $"{path}:{name}", flags, BlueprintBytecodeStatus.Decoded,
        parameters, instructions, [], null, false, true);

    private static BlueprintRecoveryInstruction Field(int offset, string owner) => Op(offset, "EX_InstanceVariable", "Count") with
    {
        ReferenceOwner = owner, ReferenceKind = BlueprintRecoveryScan.BlueprintInstanceProperty
    };

    private static BlueprintRecoveryInstruction VoidReturn(int offset) =>
        Op(offset, "EX_Return", operands: [Op(offset + 1, "EX_Nothing")]);

    private static BlueprintRecoveryInstruction Op(int offset, string opcode, string? reference = null, object? value = null,
        uint? target = null, BlueprintRecoveryInstruction[]? operands = null) => new(offset, opcode, operands ?? [], reference, value, target);
}
