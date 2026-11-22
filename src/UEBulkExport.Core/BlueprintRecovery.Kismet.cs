using System.Collections.Concurrent;
using System.Reflection;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.UObject;

namespace UEBulkExport;

internal sealed partial class BlueprintRecoveryScan
{
    internal const string BlueprintFunctionImport = "blueprint-function-import";
    internal const string BlueprintDefaultObjectImport = "blueprint-default-object-import";
    internal const string BlueprintInstanceProperty = "blueprint-instance-property";
    internal const string BlueprintLocalFunction = "blueprint-local-function";
    private const int MaxExpressionDepth = 40;
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> OperandFields = new();

    internal (IReadOnlyList<BlueprintRecoveryInstruction> Instructions, BlueprintBytecodeStatus Status, string? Diagnostic)
        ReadInstructions(UFunction function)
    {
        if (function.FunctionFlags.HasFlag(EFunctionFlags.FUNC_Native))
            return ([], BlueprintBytecodeStatus.Native, "The native implementation is outside the cooked Blueprint.");
        if (function.ScriptBytecode is null)
            return ([], BlueprintBytecodeStatus.Absent, "The export supplies no decoded Kismet script.");

        var instructions = new List<BlueprintRecoveryInstruction>();
        var complete = true;
        foreach (var expression in function.ScriptBytecode)
        {
            var instruction = ProjectInstruction(expression, 0, ref complete);
            if (instruction is not null) instructions.Add(instruction);
            if (!complete) break;
        }
        if (!complete)
            return (instructions, BlueprintBytecodeStatus.Truncated, "The report reached its instruction count or nesting limit.");

        // The upstream parser retains a decoded prefix when an opcode fails. A successful script
        // ends in EX_EndOfScript. An empty prefix or a missing final marker cannot prove absence.
        return function.ScriptBytecode.LastOrDefault() is EX_EndOfScript
            ? (instructions, BlueprintBytecodeStatus.Decoded, null)
            : (instructions, BlueprintBytecodeStatus.Unparsed, "CUE4Parse did not return a complete script ending in EX_EndOfScript.");
    }

    private BlueprintRecoveryInstruction? ProjectInstruction(KismetExpression expression, int depth, ref bool complete)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth >= MaxExpressionDepth || _instructions >= limits.MaxInstructions)
        {
            _instructionLimit = true;
            complete = false;
            return null;
        }
        _instructions++;

        var operands = new List<BlueprintRecoveryInstruction>();
        foreach (var child in ReadOperands(expression))
        {
            var operand = ProjectInstruction(child, depth + 1, ref complete);
            if (operand is not null) operands.Add(operand);
            if (!complete) break;
        }
        if (expression is EX_FinalFunction call)
            return ProjectFunctionReference(call, operands);
        if (expression is EX_ObjectConst constant)
            return ProjectObjectReference(constant, operands);
        var instruction = new BlueprintRecoveryInstruction(expression.StatementIndex, expression.Token.ToString(), operands,
            ReadReference(expression), ReadScalar(expression), ReadTarget(expression));
        var property = expression switch
        {
            EX_VariableBase variable => variable.Variable,
            EX_LetValueOnPersistentFrame assignment => assignment.DestinationProperty,
            _ => null
        };
        if (property is null || !TryResolvePropertyOwner(property, out var owner) || ObjectPath(owner) is not { } ownerPath)
            return instruction;
        var kind = expression is EX_InstanceVariable && owner is { ExportIndex: >= 0 } &&
                   owner.Class?.Name.Text.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal) == true
            ? BlueprintInstanceProperty : null;
        return instruction with { ReferenceOwner = ownerPath, ReferenceKind = kind };
    }

    private static bool TryResolvePropertyOwner(FKismetPropertyPointer pointer, out ResolvedObject? owner)
    {
        owner = pointer.bNew
            ? pointer.New is { Path.Length: 1 } field ? field.ResolvedOwner?.ResolvedObject : null
            : pointer.Old?.ResolvedObject?.Outer;
        return owner is not null;
    }

    private static BlueprintRecoveryInstruction ProjectObjectReference(EX_ObjectConst constant,
        IReadOnlyList<BlueprintRecoveryInstruction> operands)
    {
        var reference = constant.Value;
        var target = reference.ResolvedObject;
        var path = ObjectPath(target);
        var instruction = new BlueprintRecoveryInstruction(constant.StatementIndex, constant.Token.ToString(), operands,
            path, null, null);
        if (path is null || !reference.IsImport || target is not { ExportIndex: >= 0 } ||
            !HasClassDefaultObjectFlag(target) || target.Class is not { ExportIndex: >= 0 } owner ||
            owner.Class?.Name.Text.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal) != true ||
            string.Equals(target.Package.Name, reference.Owner?.Name, StringComparison.OrdinalIgnoreCase) ||
            ObjectPath(owner) is not { } ownerPath)
            return instruction;
        return instruction with { ReferenceOwner = ownerPath, ReferenceName = Clip(target.Name.Text), ReferenceKind = BlueprintDefaultObjectImport };
    }

    private static bool HasClassDefaultObjectFlag(ResolvedObject target) => target.Package switch
    {
        Package package when target.ExportIndex < package.ExportMap.Length =>
            ((EObjectFlags)package.ExportMap[target.ExportIndex].ObjectFlags).HasFlag(EObjectFlags.RF_ClassDefaultObject),
        IoPackage package when target.ExportIndex < package.ExportMap.Length =>
            package.ExportMap[target.ExportIndex].ObjectFlags.HasFlag(EObjectFlags.RF_ClassDefaultObject),
        _ => false
    };

    private static BlueprintRecoveryInstruction ProjectFunctionReference(EX_FinalFunction call,
        IReadOnlyList<BlueprintRecoveryInstruction> operands)
    {
        var reference = call.StackNode;
        var target = reference.ResolvedObject;
        var path = ObjectPath(target);
        var instruction = new BlueprintRecoveryInstruction(call.StatementIndex, call.Token.ToString(), operands,
            path ?? $"package index {reference.Index}", null, null);
        if (target?.Outer is not { } owner || path is null || ObjectPath(owner) is not { } ownerPath) return instruction;
        instruction = instruction with { ReferenceOwner = ownerPath, ReferenceName = Clip(target.Name.Text) };
        if (target.ExportIndex < 0 || target.Class?.Name.Text != "Function" || owner.ExportIndex < 0 ||
            owner.Class?.Name.Text.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal) != true)
            return instruction;
        if (reference.IsExport && ReferenceEquals(target.Package, reference.Owner))
            return instruction with { ReferenceKind = BlueprintLocalFunction };
        if (!reference.IsImport || string.Equals(target.Package.Name, reference.Owner?.Name, StringComparison.OrdinalIgnoreCase))
            return instruction;
        return instruction with { ReferenceKind = BlueprintFunctionImport };
    }

    private static IEnumerable<KismetExpression> ReadOperands(KismetExpression expression)
    {
        if (expression is EX_Let let)
        {
            yield return let.Variable;
            yield return let.Assignment;
            yield break;
        }
        if (expression is EX_LetBase typedLet)
        {
            yield return typedLet.Variable;
            yield return typedLet.Assignment;
            yield break;
        }
        if (expression is EX_Context context)
        {
            yield return context.ObjectExpression;
            yield return context.ContextExpression;
            yield break;
        }
        if (expression is EX_SwitchValue select)
        {
            yield return select.IndexTerm;
            foreach (var item in select.Cases)
            {
                yield return item.CaseIndexValueTerm;
                yield return item.CaseTerm;
            }
            yield return select.DefaultTerm;
            yield break;
        }

        // The upstream types already contain the parse tree. Read only their public expression
        // fields, so reporting never recurses through arbitrary UObject references or native data.
        foreach (var field in OperandFields.GetOrAdd(expression.GetType(), static type => type
                     .GetFields(BindingFlags.Public | BindingFlags.Instance)
                     .Where(field => typeof(KismetExpression).IsAssignableFrom(field.FieldType) ||
                                     typeof(KismetExpression[]).IsAssignableFrom(field.FieldType)).ToArray()))
        {
            switch (field.GetValue(expression))
            {
                case KismetExpression child:
                    yield return child;
                    break;
                case KismetExpression[] children:
                    foreach (var item in children) yield return item;
                    break;
            }
        }
    }

    private static string? ReadReference(KismetExpression expression) => expression switch
    {
        EX_VariableBase variable => Clip(variable.Variable.ToString()),
        EX_VirtualFunction function => Clip(function.VirtualFunctionName.Text),
        EX_StructMemberContext context => Clip(context.Property.ToString()),
        EX_LetValueOnPersistentFrame assignment => Clip(assignment.DestinationProperty.ToString()),
        _ => null
    };

    private static object? ReadScalar(KismetExpression expression)
    {
        object? result = expression switch
        {
            EX_True => true,
            EX_False => false,
            EX_IntZero => 0,
            EX_IntOne => 1,
            EX_IntConst value => value.Value,
            EX_IntConstByte value => (int)value.Value,
            EX_Int64Const value => value.Value,
            EX_UInt64Const value => value.Value,
            EX_FloatConst value => value.Value,
            EX_DoubleConst value => value.Value,
            EX_ByteConst value => value.Value,
            EX_StringConst value => value.Value,
            EX_UnicodeStringConst value => value.Value,
            EX_NameConst value => value.Value.Text,
            _ => null
        };
        return IsScalar(result) ? result : null;
    }

    private static uint? ReadTarget(KismetExpression expression) => expression switch
    {
        EX_Skip => null, // relative expression skip, not an absolute statement target
        EX_Jump jump => jump.CodeOffset,
        EX_PushExecutionFlow push => push.PushingAddress,
        EX_SkipOffsetConst skip => skip.Value,
        EX_SwitchValue select => select.EndGotoOffset,
        EX_Context context when context.ContextExpression.StatementIndex >= 0 &&
                                context.Offset <= uint.MaxValue - (uint)context.ContextExpression.StatementIndex =>
            (uint)context.ContextExpression.StatementIndex + context.Offset,
        _ => null
    };

    internal static IReadOnlyList<BlueprintRecoveryEdge> BuildFlow(IReadOnlyList<BlueprintRecoveryInstruction> instructions)
    {
        var edges = new List<BlueprintRecoveryEdge>();
        var known = instructions.Select(instruction => (uint)instruction.Offset).ToHashSet();
        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            var following = index + 1 < instructions.Count ? (uint?) instructions[index + 1].Offset : null;
            void Add(uint? target, string kind) => edges.Add(new BlueprintRecoveryEdge(instruction.Offset, target,
                kind, target is uint offset && known.Contains(offset)));

            switch (instruction.Opcode)
            {
                case nameof(EExprToken.EX_Return):
                case nameof(EExprToken.EX_EndOfScript):
                    break;
                case nameof(EExprToken.EX_Jump):
                    Add(instruction.TargetOffset, "jump");
                    break;
                case nameof(EExprToken.EX_JumpIfNot):
                    Add(instruction.TargetOffset, "when-false");
                    Add(following, "when-true");
                    break;
                case nameof(EExprToken.EX_Skip):
                    Add(null, "relative-target");
                    break;
                case nameof(EExprToken.EX_ComputedJump):
                case nameof(EExprToken.EX_PopExecutionFlow):
                    Add(null, "dynamic");
                    break;
                case nameof(EExprToken.EX_PopExecutionFlowIfNot):
                    Add(null, "when-false-dynamic");
                    Add(following, "when-true");
                    break;
                case nameof(EExprToken.EX_PushExecutionFlow):
                    Add(instruction.TargetOffset, "deferred-stack-target");
                    Add(following, "next");
                    break;
                default:
                    if (following is not null) Add(following, "next");
                    break;
            }
        }
        return edges;
    }
}
