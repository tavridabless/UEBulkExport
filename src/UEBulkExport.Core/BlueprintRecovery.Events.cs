using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;

namespace UEBulkExport;

public static partial class BlueprintRecovery
{
    private const int MaxEventPathInstructions = 8;

    private static BlueprintRecoveryFunction? FindEventDispatcher(BlueprintRecoveryClass source, BlueprintRecoveryFunction function)
    {
        var path = function.EventGraphFunction;
        if (path is null)
        {
            // Ordinary functions can call an event, but only the generated local ubergraph
            // export identifies the event wrapper. A similar function name is insufficient.
            path = function.Instructions.FirstOrDefault(instruction => instruction is
            {
                Opcode: nameof(EExprToken.EX_LocalFinalFunction), ReferenceKind: BlueprintRecoveryScan.BlueprintLocalFunction
            } && instruction.ReferenceOwner == source.Path &&
                 instruction.ReferenceName?.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal) == true)?.Reference;
        }
        return path is null ? null : source.Functions.FirstOrDefault(candidate => candidate.Path == path &&
            Enum.TryParse<EFunctionFlags>(candidate.Flags, out var flags) && flags.HasFlag(EFunctionFlags.FUNC_UbergraphFunction));
    }

    private static EventProof? ReadCustomEventBody(BlueprintRecoveryClass source,
        IReadOnlyList<BlueprintReconstructionVariable> variables, BlueprintRecoveryFunction function, BlueprintRecoveryFunction dispatcher)
    {
        const EFunctionFlags customEvent = EFunctionFlags.FUNC_BlueprintCallable | EFunctionFlags.FUNC_BlueprintEvent;
        if (!CanMatchBody(function) || !CanMatchBody(dispatcher) || function.Pure ||
            function.SuperFunction is not null || !Enum.TryParse<EFunctionFlags>(function.Flags, out var flags) || flags != customEvent ||
            dispatcher.Parameters is not [{ Name: "EntryPoint", Type: "IntProperty", ArrayDimension: 1,
                Direction: BlueprintParameterDirection.Input }] ||
            function.Parameters.Any(parameter => parameter.Direction != BlueprintParameterDirection.Input || parameter.ArrayDimension != 1 ||
                !IsIdentifier(parameter.Name) || GetReconstructionType(parameter.Type) is null))
            return null;

        if (function.Instructions is not [..,
                { Opcode: nameof(EExprToken.EX_LocalFinalFunction), ReferenceKind: BlueprintRecoveryScan.BlueprintLocalFunction,
                    Operands: [var entry] } dispatch,
                var wrapperReturn,
                { Opcode: nameof(EExprToken.EX_EndOfScript), Operands.Count: 0 } wrapperEnd
            ] || function.Instructions[0].Offset != 0 || dispatch.Reference != dispatcher.Path || dispatch.ReferenceOwner != source.Path ||
            entry.Value is not int entryOffset || !IsLiteral(entry, "int") || entryOffset < 0 ||
            dispatch.Offset >= entry.Offset || !IsVoidReturn(wrapperReturn) || wrapperReturn.Offset <= entry.Offset || wrapperEnd.Offset <= wrapperReturn.Offset ||
            (function.EventGraphFunction is { } path && (path != dispatcher.Path || function.EventGraphCallOffset != entryOffset)))
            return null;

        var bindings = ReadEventFrameBindings(function, dispatcher);
        if (bindings is null || dispatcher.Instructions is not [
                { Offset: 0, Opcode: nameof(EExprToken.EX_ComputedJump), Operands: [var index] }, ..,
                { Opcode: nameof(EExprToken.EX_EndOfScript), Operands.Count: 0 } end
            ] || !IsInput(index, "EntryPoint") || index.ReferenceOwner != dispatcher.Path)
            return null;

        // Follow each authored entry separately. A different event can use the same ubergraph,
        // but its statements and frame bindings cannot establish this event's implementation.
        var instructions = dispatcher.Instructions;
        var covered = new HashSet<int> { 0, end.Offset };
        var position = FindOffset(instructions, entryOffset);
        BlueprintReconstructionBody? body = null;
        for (var steps = 0; position > 0 && position < instructions.Count && steps < MaxEventPathInstructions; steps++)
        {
            var statement = instructions[position];
            if (!covered.Add(statement.Offset)) return null;
            if (IsVoidReturn(statement)) return body is null ? null : new EventProof(body, covered);
            if (statement is { Opcode: nameof(EExprToken.EX_Jump), Operands.Count: 0, TargetOffset: <= int.MaxValue } jump)
            {
                position = FindOffset(instructions, (int)jump.TargetOffset!.Value);
                continue;
            }
            if (body is not null || ReadEventAssignment(source, variables, statement, dispatcher.Path, bindings) is not { } assignment)
                return null;
            body = assignment;
            position++;
        }
        return null;
    }

    private static Dictionary<string, BlueprintRecoveryParameter>? ReadEventFrameBindings(BlueprintRecoveryFunction function,
        BlueprintRecoveryFunction dispatcher)
    {
        var parameters = new Dictionary<string, BlueprintRecoveryParameter>(StringComparer.Ordinal);
        foreach (var parameter in function.Parameters)
            if (!parameters.TryAdd(parameter.Name, parameter)) return null;

        var bindings = new Dictionary<string, BlueprintRecoveryParameter>(StringComparer.Ordinal);
        var prefixLength = function.Instructions.Count - 3;
        if (prefixLength != parameters.Count) return null;
        for (var position = 0; position < prefixLength; position++)
        {
            var copy = function.Instructions[position];
            if (copy is not { Opcode: nameof(EExprToken.EX_LetValueOnPersistentFrame), Reference: { } slot, Operands: [var argument] } ||
                copy.ReferenceOwner != dispatcher.Path || argument.Reference is not { } name || !IsInput(argument, name) ||
                argument.ReferenceOwner != function.Path || !parameters.Remove(name, out var parameter) ||
                !dispatcher.LocalVariables.Any(local => local.Name == slot && local.Type == parameter.Type && local.ArrayDimension == 1) ||
                !bindings.TryAdd(slot, parameter))
                return null;
        }
        return bindings;
    }

    private static BlueprintReconstructionBody? ReadEventAssignment(BlueprintRecoveryClass source,
        IReadOnlyList<BlueprintReconstructionVariable> variables, BlueprintRecoveryInstruction statement, string dispatcherPath,
        IReadOnlyDictionary<string, BlueprintRecoveryParameter> bindings)
    {
        if (statement is not { Operands: [_, var right] }) return null;
        if (bindings.Count == 0) return ReadInstancePropertyWrite(source, variables, statement, []);
        if (right.ReferenceOwner != dispatcherPath || right.Reference is not { } name || !bindings.TryGetValue(name, out var input)) return null;
        var body = ReadInstancePropertyWrite(source, variables, statement, [input with { Name = name }]);
        return body is null ? null : body with { Parameter = input.Name };
    }

    private static int FindOffset(IReadOnlyList<BlueprintRecoveryInstruction> instructions, int offset)
    {
        var low = 0;
        var high = instructions.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var current = instructions[middle].Offset;
            if (current == offset) return middle;
            if (current < offset) low = middle + 1;
            else high = middle - 1;
        }
        return -1;
    }

    private static bool IsVoidReturn(BlueprintRecoveryInstruction instruction) => instruction is
    {
        Opcode: nameof(EExprToken.EX_Return), Operands: [{ Opcode: nameof(EExprToken.EX_Nothing), Operands.Count: 0 }]
    };

    private sealed record EventProof(BlueprintReconstructionBody Body, IReadOnlySet<int> Covered);
}
