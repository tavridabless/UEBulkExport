using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;

namespace UEBulkExport;

public static partial class BlueprintRecovery
{
    private static BlueprintReconstructionBody? GetInstanceCallReturn(BlueprintRecoveryClass source,
        BlueprintRecoveryFunction function, IReadOnlyList<BlueprintRecoveryParameter> parameters,
        IReadOnlyDictionary<string, BlueprintRecoveryClass> availableClasses)
    {
        if (source.Parent == FunctionLibraryParent || !CanMatchBody(function) || !TryReadInputAndReturn(parameters, out var input, out var output) ||
            input is not { Type: "ObjectProperty", TypeReference: { } targetPath } ||
            targetPath == source.Path || !availableClasses.TryGetValue(targetPath, out var target) ||
            target is not { Parent: ActorParent, MetadataComplete: true } || target.Path != targetPath ||
            ReadContextualReturn(function, output) is not { } context || !IsInput(context.Receiver, input.Name) ||
            context.Receiver.ReferenceOwner != function.Path ||
            context.Call is not { Operands.Count: 0, Reference: { } method } || !IsIdentifier(method))
            return null;

        var member = target.Functions.FirstOrDefault(candidate => candidate.Name == method && candidate.Path == $"{targetPath}:{method}");
        if (member is not { Pure: true, Parameters: [var result] } || !CanMatchBody(member) || !CanRecreateFlags(member, false) ||
            !IsReturnParameter(result) || result.Type != output.Type || !Enum.TryParse<EFunctionFlags>(member.Flags, out var flags) ||
            !flags.HasFlag(EFunctionFlags.FUNC_Public | EFunctionFlags.FUNC_BlueprintCallable) || flags.HasFlag(EFunctionFlags.FUNC_Final))
            return null;

        // The target graph must itself be rebuilt, so a valid signature alone cannot turn
        // an unknown or stubbed callee into a confirmed runtime operation.
        BlueprintReconstructionPlan targetPlan;
        try { targetPlan = CreateReconstruction(target); }
        catch (UserFacingException) { return null; }
        if (!targetPlan.Functions.Any(candidate => candidate.Name == method && candidate.Body.Kind is "property-read" or "constant-return"))
            return null;
        return new BlueprintReconstructionBody("instance-call-return", null, null, TargetClass: targetPath,
            TargetFunction: method, Arguments: [], Receiver: input.Name);
    }

    private static BlueprintReconstructionBody? GetInstancePropertyBody(BlueprintRecoveryClass source,
        IReadOnlyList<BlueprintReconstructionVariable> variables, BlueprintRecoveryFunction function,
        IReadOnlyList<BlueprintRecoveryParameter> parameters)
    {
        if (source.Parent == FunctionLibraryParent || !CanMatchBody(function)) return null;
        if (parameters is [var output] && IsReturnParameter(output) &&
            ReadReturnExpression(function.Instructions, output.Name) is { } expression &&
            FindInstanceProperty(source, variables, expression) is { } read && read.Type == GetReconstructionType(output.Type))
            return new BlueprintReconstructionBody("property-read", null, null, Property: read.Name);

        if (function.Pure || parameters.Count > 1 || function.Instructions is not [
                { Offset: 0 } assignment,
                { Opcode: nameof(EExprToken.EX_Return), Operands: [{ Opcode: nameof(EExprToken.EX_Nothing), Operands.Count: 0 }] } returned,
                { Opcode: nameof(EExprToken.EX_EndOfScript), Operands.Count: 0 } end
            ] || returned.Offset <= assignment.Offset || end.Offset <= returned.Offset)
            return null;
        return ReadInstancePropertyWrite(source, variables, assignment, parameters);
    }

    private static BlueprintReconstructionBody? ReadInstancePropertyWrite(BlueprintRecoveryClass source,
        IReadOnlyList<BlueprintReconstructionVariable> variables, BlueprintRecoveryInstruction assignment,
        IReadOnlyList<BlueprintRecoveryParameter> parameters)
    {
        if (assignment is not
            {
                Opcode: nameof(EExprToken.EX_Let) or nameof(EExprToken.EX_LetBool),
                Operands: [var destination, var expression]
            } || FindInstanceProperty(source, variables, destination) is not { } property)
            return null;
        if (parameters.Count == 0 && IsLiteral(expression, property.Type))
            return new BlueprintReconstructionBody("property-write", expression.Value, null, Property: property.Name);
        if (parameters is [{ Direction: BlueprintParameterDirection.Input, ArrayDimension: 1 } input] &&
            GetReconstructionType(input.Type) == property.Type && IsInput(expression, input.Name))
            return new BlueprintReconstructionBody("property-write", null, null, Parameter: input.Name, Property: property.Name);
        return null;
    }

    private static BlueprintReconstructionVariable? FindInstanceProperty(BlueprintRecoveryClass source,
        IReadOnlyList<BlueprintReconstructionVariable> variables, BlueprintRecoveryInstruction instruction)
    {
        if (instruction is not
            {
                Opcode: nameof(EExprToken.EX_InstanceVariable), Operands.Count: 0,
                ReferenceKind: BlueprintRecoveryScan.BlueprintInstanceProperty, Reference: { } name
            } || instruction.ReferenceOwner != source.Path)
            return null;
        var field = source.Variables.FirstOrDefault(variable => variable.Name == name);
        if (field is null || !Enum.TryParse<EPropertyFlags>(field.Flags, out var flags) ||
            (flags & (EPropertyFlags.Net | EPropertyFlags.RepNotify | EPropertyFlags.Config | EPropertyFlags.Transient)) != 0)
            return null;
        return variables.FirstOrDefault(variable => variable.Name == name);
    }
}
