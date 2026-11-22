using System.Text.Json.Serialization;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;

namespace UEBulkExport;

public static partial class BlueprintRecovery
{
    internal const string ActorParent = "/Script/Engine.Actor";
    internal const string FunctionLibraryParent = "/Script/Engine.BlueprintFunctionLibrary";

    public static void WriteReconstruction(BlueprintRecoveryClass source, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        WriteJson(CreateReconstruction(source), path);
    }

    internal static BlueprintReconstructionPlan CreateReconstruction(BlueprintRecoveryClass source,
        IReadOnlyDictionary<string, BlueprintRecoveryClass>? availableClasses = null)
    {
        var isLibrary = source.Parent == FunctionLibraryParent;
        var ancestors = isLibrary ? [] : FindActorAncestors(source, availableClasses)
            ?? throw new UserFacingException("The Blueprint has no complete supported Actor parent chain in this plan.",
                "Scan the parent classes and use a linked or batch plan.");
        var name = ReconstructionName(source);
        if (!IsIdentifier(name) || !source.MetadataComplete)
            throw new UserFacingException("The Blueprint metadata is incomplete or its name cannot be recreated.",
                "Narrow the source filter and use a report to inspect the available data.");

        var variables = new List<BlueprintReconstructionVariable>();
        var functions = new List<BlueprintReconstructionFunction>();
        var omissions = new List<string>();
        var inheritedMembers = ancestors.SelectMany(parent => parent.Variables.Select(variable => variable.Name)
            .Concat(parent.Functions.Select(function => function.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var members = new HashSet<string>(inheritedMembers, StringComparer.OrdinalIgnoreCase);
        var overrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = ProjectComponents(source, members, omissions);
        foreach (var variable in source.Variables)
        {
            if (IsComponentVariable(source, variable) || IsCompilerEventFrame(variable)) continue;
            var type = GetReconstructionType(variable.Type);
            if (isLibrary || variable.ArrayDimension != 1 || type is null || !IsIdentifier(variable.Name) || !members.Add(variable.Name))
            {
                omissions.Add($"Variable {variable.Name}: unsupported name or type {variable.Type}.");
                continue;
            }
            var defaultValue = GetReconstructionDefault(variable, type);
            if (defaultValue is null)
                omissions.Add($"Variable {variable.Name}: original default value could not be carried into the plan.");
            variables.Add(new BlueprintReconstructionVariable(variable.Name, type, defaultValue));
        }

        var events = new List<BlueprintReconstructionEvent>();
        var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dispatcherCoverage = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var recreatedDispatchers = 0;
        if (!isLibrary)
        {
            foreach (var function in source.Functions)
            {
                if (FindEventDispatcher(source, function) is not { } dispatcher) continue;
                represented.Add(function.Path);
                var proof = ReadCustomEventBody(source, variables, function, dispatcher);
                if (proof is null || !IsIdentifier(function.Name) || !members.Add(function.Name))
                {
                    omissions.Add(function.Parameters.Count > 0
                        ? $"Event {function.Name}: unsupported event parameters, frame bindings, body, or name."
                        : $"Event {function.Name}: unsupported cooked event body or name.");
                    continue;
                }
                events.Add(new BlueprintReconstructionEvent(function.Name, function.Parameters.Select(ToPin).ToArray(), proof.Body));
                if (!dispatcherCoverage.TryGetValue(dispatcher.Path, out var covered))
                    dispatcherCoverage.Add(dispatcher.Path, covered = []);
                covered.UnionWith(proof.Covered);
            }
        }

        foreach (var function in source.Functions)
        {
            if (represented.Contains(function.Path)) continue;
            if (dispatcherCoverage.TryGetValue(function.Path, out var covered) && covered.Count == function.Instructions.Count)
            {
                recreatedDispatchers++;
                continue;
            }
            var overrideClass = FindOverrideClass(ancestors, function);
            if (overrideClass is null && inheritedMembers.Contains(function.Name))
            {
                omissions.Add($"Function {function.Name}: unsupported override of an inherited member.");
                continue;
            }
            var parameters = PlanParameters(function, isLibrary);
            if (function.Name == "UserConstructionScript" ||
                function.Name.StartsWith("ExecuteUbergraph_", StringComparison.OrdinalIgnoreCase) ||
                !CanRecreateFlags(function, isLibrary))
            {
                omissions.Add($"Function {function.Name}: built-in, native, event, networking, delegate, or static function.");
                continue;
            }
            if (!function.ParametersComplete || !IsIdentifier(function.Name) || parameters.Any(parameter =>
                    parameter.ArrayDimension != 1 || !IsIdentifier(parameter.Name) || parameter.Direction == BlueprintParameterDirection.Reference))
            {
                omissions.Add($"Function {function.Name}: unsupported or incomplete signature.");
                continue;
            }

            var body = InferBody(source, variables, function, parameters, availableClasses);
            if (overrideClass is not null && body.Kind is not ("constant-return" or "parameter-return"))
            {
                omissions.Add($"Function {function.Name}: unsupported body for a Blueprint override.");
                continue;
            }
            if (parameters.Any(parameter => GetReconstructionType(parameter.Type) is null &&
                                            (body.Kind != "instance-call-return" || parameter.Name != body.Receiver)) ||
                !(overrideClass is null ? members : overrides).Add(function.Name))
            {
                omissions.Add($"Function {function.Name}: unsupported or incomplete signature.");
                continue;
            }
            var inputs = parameters.Where(parameter => parameter.Direction == BlueprintParameterDirection.Input)
                .Select(parameter => parameter.Name == body.Receiver
                    ? new BlueprintReconstructionPin(parameter.Name, "object", body.TargetClass) : ToPin(parameter)).ToArray();
            var outputs = parameters.Where(parameter => parameter.Direction is BlueprintParameterDirection.Output or BlueprintParameterDirection.Return)
                .Select(ToPin).ToArray();
            functions.Add(new BlueprintReconstructionFunction(function.Name, inputs, outputs, function.Pure, body)
            {
                OverrideClass = overrideClass
            });
        }
        var parentClass = ancestors.FirstOrDefault()?.Path ?? source.Parent!;
        var componentOverrides = ProjectComponentOverrides(source, ancestors, omissions);
        var inheritedDefaults = PlanInheritedDefaults(source, ancestors, omissions);
        return new BlueprintReconstructionPlan(1, source.Path, name, parentClass, variables, functions, omissions)
        {
            Components = components.Count > 0 ? components : null,
            ComponentOverrides = componentOverrides.Count > 0 ? componentOverrides : null,
            Events = events.Count > 0 ? events : null,
            InheritedDefaults = inheritedDefaults.Count > 0 ? inheritedDefaults : null,
            RecreatedCompilerFunctions = recreatedDispatchers
        };
    }

    private static BlueprintReconstructionBody InferBody(BlueprintRecoveryClass source, IReadOnlyList<BlueprintReconstructionVariable> variables,
        BlueprintRecoveryFunction function, IReadOnlyList<BlueprintRecoveryParameter> parameters,
        IReadOnlyDictionary<string, BlueprintRecoveryClass>? availableClasses)
    {
        if (GetConstantReturn(function, parameters) is { } constant) return new BlueprintReconstructionBody("constant-return", constant, null);
        if (GetParameterReturn(function, parameters) is { } parameter) return new BlueprintReconstructionBody("parameter-return", null, null, parameter);
        if (GetConditionalReturn(function) is { } branch)
            return new BlueprintReconstructionBody("conditional-return", null, null, Condition: branch.Condition,
                WhenTrue: branch.WhenTrue, WhenFalse: branch.WhenFalse);
        if (GetInstancePropertyBody(source, variables, function, parameters) is { } property)
            return property;
        if (availableClasses is not null && GetFunctionCallReturn(function, parameters, availableClasses) is { } call)
            return call;
        if (availableClasses is not null && GetInstanceCallReturn(source, function, parameters, availableClasses) is { } instanceCall)
            return instanceCall;
        return new BlueprintReconstructionBody("stub", null,
            function.Diagnostic ?? "The cooked implementation is outside the supported reconstruction subset.");
    }

    private static BlueprintReconstructionBody? GetFunctionCallReturn(BlueprintRecoveryFunction function,
        IReadOnlyList<BlueprintRecoveryParameter> parameters, IReadOnlyDictionary<string, BlueprintRecoveryClass> availableClasses)
    {
        if (!CanMatchBody(function) || !TryReadInputAndReturn(parameters, out var input, out var output) ||
            GetReconstructionType(input.Type) is null || ReadContextualReturn(function, output) is not { } evidence ||
            evidence.Receiver is not { Opcode: nameof(EExprToken.EX_ObjectConst), Operands.Count: 0,
                ReferenceKind: BlueprintRecoveryScan.BlueprintDefaultObjectImport, ReferenceOwner: { } owner, Reference: { } defaultObject } ||
            !availableClasses.TryGetValue(owner, out var targetClass) ||
            targetClass is not { Parent: FunctionLibraryParent, MetadataComplete: true } ||
            targetClass.Path != owner || targetClass.DefaultObject != defaultObject ||
            evidence.Call.Reference is not { } targetName || !IsIdentifier(targetName))
            return null;

        var targetPath = $"{targetClass.Path}:{targetName}";
        var target = targetClass.Functions.FirstOrDefault(candidate => candidate.Name == targetName && candidate.Path == targetPath);
        if (target is null || !CanMatchBody(target) || !CanRecreateFlags(target, true) ||
            !Enum.TryParse<EFunctionFlags>(target.Flags, out var flags) || !flags.HasFlag(EFunctionFlags.FUNC_Public | EFunctionFlags.FUNC_BlueprintCallable))
            return null;
        var targetParameters = PlanParameters(target, true);
        if (!TryReadInputAndReturn(targetParameters, out var targetInput, out var targetOutput) ||
            !IsIdentifier(targetInput.Name) || targetInput.Type != input.Type || targetOutput.Type != output.Type ||
            GetParameterReturn(target, targetParameters) is null)
            return null;

        // UE inserts a hidden context parameter for static Blueprint graphs. Its meaning is only
        // preserved here when the caller supplied self and the proven target body never reads it.
        var arguments = target.Parameters.Where(parameter => parameter.Direction == BlueprintParameterDirection.Input).ToArray();
        if (arguments.Length != evidence.Call.Operands.Count) return null;
        for (var index = 0; index < arguments.Length; index++)
        {
            var operand = evidence.Call.Operands[index];
            var matches = IsWorldContext(arguments[index])
                ? operand is { Opcode: nameof(EExprToken.EX_Self), Operands.Count: 0 }
                : IsInput(operand, input.Name);
            if (!matches) return null;
        }
        return new BlueprintReconstructionBody("function-call-return", null, null, TargetClass: targetClass.Path,
            TargetFunction: target.Name, Arguments: [new BlueprintReconstructionArgument(input.Name)]);
    }

    private static (BlueprintRecoveryInstruction Receiver, BlueprintRecoveryInstruction Call)? ReadContextualReturn(
        BlueprintRecoveryFunction function, BlueprintRecoveryParameter output)
    {
        // A local virtual instruction only carries a name. Retain the concrete receiver
        // and require the complete assignment, read-back and return before matching a call.
        if (function.Instructions is not [
                { Offset: 0, Opcode: nameof(EExprToken.EX_Let), Operands: [var temporary, var context] },
                var assignReturn,
                { Opcode: nameof(EExprToken.EX_Return), Operands: [var result] } returned,
                { Opcode: nameof(EExprToken.EX_EndOfScript), Operands.Count: 0 } end
            ] || context is not { Opcode: nameof(EExprToken.EX_Context), Operands: [var receiver, var call] } ||
            call.Opcode != nameof(EExprToken.EX_LocalVirtualFunction) ||
            temporary.Reference is not { } local || !IsInput(temporary, local) ||
            function.Parameters.Any(parameter => local.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)) ||
            !function.LocalVariables.Any(variable => variable.Name == local && variable.ArrayDimension == 1 && variable.Type == output.Type) ||
            ReadOutputAssignment(assignReturn, output.Name) is not { } readTemporary || !IsInput(readTemporary, local) ||
            !IsOutput(result, output.Name) || context.TargetOffset != (uint)assignReturn.Offset ||
            !(assignReturn.Offset > call.Offset && returned.Offset > assignReturn.Offset && end.Offset > returned.Offset))
            return null;
        return (receiver, call);
    }

    internal static object? GetConstantReturn(BlueprintRecoveryFunction function) => GetConstantReturn(function, function.Parameters);

    private static object? GetConstantReturn(BlueprintRecoveryFunction function, IReadOnlyList<BlueprintRecoveryParameter> parameters)
    {
        if (!CanMatchBody(function) || parameters is not [var output] || !IsReturnParameter(output))
            return null;
        var expression = ReadReturnExpression(function.Instructions, output.Name);
        return expression is not null && IsLiteral(expression, GetReconstructionType(output.Type)!) ? expression.Value : null;
    }

    internal static string? GetParameterReturn(BlueprintRecoveryFunction function) => GetParameterReturn(function, function.Parameters);

    private static string? GetParameterReturn(BlueprintRecoveryFunction function, IReadOnlyList<BlueprintRecoveryParameter> parameters)
    {
        if (!CanMatchBody(function) || !TryReadInputAndReturn(parameters, out var input, out var output) || input.Type != output.Type) return null;
        var expression = ReadReturnExpression(function.Instructions, output.Name);
        return expression is not null && IsInput(expression, input.Name) ? input.Name : null;
    }

    internal static (string Condition, object WhenTrue, object WhenFalse)? GetConditionalReturn(BlueprintRecoveryFunction function)
    {
        if (!CanMatchBody(function) || function.Pure || !TryReadInputAndReturn(function.Parameters, out var input, out var output) ||
            input.Type != "BoolProperty" || GetReconstructionType(output.Type) is not { } type)
            return null;

        // Both paths must store the same return slot and converge on one return. Treating a
        // branch as a local pattern would drop any other work the VM performs in this function.
        if (function.Instructions is not [
                { Offset: 0, Opcode: nameof(EExprToken.EX_JumpIfNot), Operands: [var condition] } branch,
                var whenTrue,
                { Opcode: nameof(EExprToken.EX_Jump), Operands.Count: 0 } jump,
                var whenFalse,
                { Opcode: nameof(EExprToken.EX_Return), Operands: [var result] } returned,
                { Opcode: nameof(EExprToken.EX_EndOfScript), Operands.Count: 0 } end
            ] || !IsInput(condition, input.Name) || !IsOutput(result, output.Name) ||
            branch.TargetOffset != (uint)whenFalse.Offset || jump.TargetOffset != (uint)returned.Offset ||
            !(whenTrue.Offset > 0 && jump.Offset > whenTrue.Offset && whenFalse.Offset > jump.Offset &&
              returned.Offset > whenFalse.Offset && end.Offset > returned.Offset))
            return null;

        var trueLiteral = ReadOutputAssignment(whenTrue, output.Name);
        var falseLiteral = ReadOutputAssignment(whenFalse, output.Name);
        return trueLiteral is { Value: { } trueValue } && IsLiteral(trueLiteral, type) &&
               falseLiteral is { Value: { } falseValue } && IsLiteral(falseLiteral, type)
            ? (input.Name, trueValue, falseValue) : null;
    }

    private static bool TryReadInputAndReturn(IReadOnlyList<BlueprintRecoveryParameter> parameters, out BlueprintRecoveryParameter input,
        out BlueprintRecoveryParameter output)
    {
        input = null!;
        output = null!;
        if (parameters.Count != 2) return false;
        var argument = parameters.FirstOrDefault(parameter => parameter.Direction == BlueprintParameterDirection.Input);
        var result = parameters.FirstOrDefault(parameter => parameter.Direction == BlueprintParameterDirection.Return);
        if (argument is not { ArrayDimension: 1 } || result is null || !IsReturnParameter(result) ||
            argument.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase)) return false;
        input = argument;
        output = result;
        return true;
    }

    private static bool CanMatchBody(BlueprintRecoveryFunction function) =>
        function.ParametersComplete && function.BytecodeStatus == BlueprintBytecodeStatus.Decoded;

    private static bool IsReturnParameter(BlueprintRecoveryParameter parameter) =>
        parameter is { Name: "ReturnValue", ArrayDimension: 1, Direction: BlueprintParameterDirection.Return } &&
        GetReconstructionType(parameter.Type) is not null;

    private static BlueprintRecoveryInstruction? ReadReturnExpression(IReadOnlyList<BlueprintRecoveryInstruction> instructions,
        string outputName)
    {
        // Match the entire body. Extra instructions, side effects, or control flow remain stubs.
        if (instructions is [
                { Opcode: nameof(EExprToken.EX_Return), Operands: [var literal] },
                { Opcode: nameof(EExprToken.EX_EndOfScript) }
            ])
            return literal;

        if (instructions is [
                { Opcode: nameof(EExprToken.EX_Let) or nameof(EExprToken.EX_LetBool), Operands: [var destination, var assigned] },
                { Opcode: nameof(EExprToken.EX_Return), Operands: [var result] },
                { Opcode: nameof(EExprToken.EX_EndOfScript) }
            ] && IsOutput(destination, outputName) && IsOutput(result, outputName))
            return assigned;
        return null;
    }

    private static BlueprintRecoveryInstruction? ReadOutputAssignment(BlueprintRecoveryInstruction instruction, string outputName) =>
        instruction is { Opcode: nameof(EExprToken.EX_Let) or nameof(EExprToken.EX_LetBool), Operands: [var destination, var value] } &&
        IsOutput(destination, outputName) ? value : null;

    private static bool IsInput(BlueprintRecoveryInstruction expression, string inputName) =>
        expression is { Opcode: nameof(EExprToken.EX_LocalVariable), Operands.Count: 0 } && expression.Reference == inputName;

    private static bool IsOutput(BlueprintRecoveryInstruction expression, string outputName) =>
        expression is { Opcode: nameof(EExprToken.EX_LocalVariable) or nameof(EExprToken.EX_LocalOutVariable), Operands.Count: 0 } &&
        expression.Reference == outputName;

    private static bool IsLiteral(BlueprintRecoveryInstruction expression, string type) =>
        expression.Operands.Count == 0 && MatchesType(expression.Value, type) && expression.Opcode is nameof(EExprToken.EX_True) or nameof(EExprToken.EX_False)
            or nameof(EExprToken.EX_IntConst) or nameof(EExprToken.EX_IntConstByte) or nameof(EExprToken.EX_IntZero) or nameof(EExprToken.EX_IntOne);

    private static BlueprintReconstructionPin ToPin(BlueprintRecoveryParameter parameter) =>
        new(parameter.Name, GetReconstructionType(parameter.Type)!);

    private static bool CanRecreateFlags(BlueprintRecoveryFunction function, bool isLibrary)
    {
        if (!Enum.TryParse<EFunctionFlags>(function.Flags, out var value) ||
            (value & (EFunctionFlags.FUNC_Event | EFunctionFlags.FUNC_Delegate | EFunctionFlags.FUNC_Net |
                      EFunctionFlags.FUNC_Native | EFunctionFlags.FUNC_UbergraphFunction)) != 0)
            return false;
        return isLibrary ? value.HasFlag(EFunctionFlags.FUNC_Static | EFunctionFlags.FUNC_BlueprintPure) && function.Pure
            : !value.HasFlag(EFunctionFlags.FUNC_Static);
    }

    private static IReadOnlyList<BlueprintRecoveryParameter> PlanParameters(BlueprintRecoveryFunction function, bool isLibrary) =>
        isLibrary && CanRecreateFlags(function, true) && function.Parameters.Count(IsWorldContext) == 1
            ? function.Parameters.Where(parameter => !IsWorldContext(parameter)).ToArray()
            : function.Parameters;

    private static bool IsWorldContext(BlueprintRecoveryParameter parameter) => parameter is
    {
        Name: "__WorldContext", Type: "ObjectProperty", TypeReference: "/Script/CoreUObject.Object", ArrayDimension: 1,
        Direction: BlueprintParameterDirection.Input
    };

    private static bool IsIdentifier(string value) => value.Length is > 0 and <= 128 &&
        !value.Equals("None", StringComparison.OrdinalIgnoreCase) &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string? GetReconstructionType(string type) => type switch
    {
        "BoolProperty" => "bool",
        "IntProperty" => "int",
        _ => null
    };

    private static bool MatchesType(object? value, string type) =>
        (type == "bool" && value is bool) || (type == "int" && value is int);

    private static bool IsCompilerEventFrame(BlueprintRecoveryVariable variable) =>
        variable is { Name: "UberGraphFrame", Type: "StructProperty", ArrayDimension: 1,
            TypeReference: "/Script/Engine.PointerToUberGraphFrame" } &&
        Enum.TryParse<EPropertyFlags>(variable.Flags, out var flags) &&
        flags == (EPropertyFlags.Transient | EPropertyFlags.DuplicateTransient);

    private static object? GetReconstructionDefault(BlueprintRecoveryVariable variable, string type)
    {
        if (variable.DefaultStatus == BlueprintDefaultStatus.Serialized)
            return MatchesType(variable.DefaultValue, type) ? variable.DefaultValue : null;
        // With a readable CDO and no serialized delta, a local, editable scalar field
        // uses the zero-initialized value of the new property. Inherited fields are handled separately.
        if (variable.DefaultStatus != BlueprintDefaultStatus.NotSerialized ||
            !Enum.TryParse<EPropertyFlags>(variable.Flags, out var flags) ||
            !flags.HasFlag(EPropertyFlags.Edit | EPropertyFlags.BlueprintVisible) ||
            (flags & (EPropertyFlags.Transient | EPropertyFlags.Config | EPropertyFlags.Net | EPropertyFlags.RepNotify)) != 0)
            return null;
        return type == "bool" ? (object)false : 0;
    }
}

internal sealed record BlueprintReconstructionPlan(int SchemaVersion, string SourceAsset, string Name, string ParentClass,
    IReadOnlyList<BlueprintReconstructionVariable> Variables, IReadOnlyList<BlueprintReconstructionFunction> Functions,
    IReadOnlyList<string> Omissions)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BlueprintReconstructionComponent>? Components { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BlueprintReconstructionComponentOverride>? ComponentOverrides { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BlueprintReconstructionEvent>? Events { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BlueprintRecoveryInheritedDefault>? InheritedDefaults { get; init; }
    [JsonIgnore]
    public int RecreatedCompilerFunctions { get; init; }
}
internal sealed record BlueprintReconstructionVariable(string Name, string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? DefaultValue);
internal sealed record BlueprintReconstructionPin(string Name, string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TypeReference = null);
internal sealed record BlueprintReconstructionFunction(string Name, IReadOnlyList<BlueprintReconstructionPin> Inputs,
    IReadOnlyList<BlueprintReconstructionPin> Outputs, bool Pure, BlueprintReconstructionBody Body)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OverrideClass { get; init; }
}
internal sealed record BlueprintReconstructionEvent(string Name, IReadOnlyList<BlueprintReconstructionPin> Inputs,
    BlueprintReconstructionBody Body);
internal sealed record BlueprintReconstructionBody(string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Parameter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Condition = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? WhenTrue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? WhenFalse = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetClass = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetFunction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BlueprintReconstructionArgument>? Arguments = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Property = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Receiver = null);
internal sealed record BlueprintReconstructionArgument(string Parameter);
