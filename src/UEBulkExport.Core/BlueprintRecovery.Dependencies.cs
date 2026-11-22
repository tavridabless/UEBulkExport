using System.Text.Json.Serialization;
using CUE4Parse.UE4.Kismet;

namespace UEBulkExport;

public enum BlueprintDependencyKind { Parent, Call, Type, Asset }
public enum BlueprintDependencyResolution { Inspected, OutsideReport, Native, Unknown }

public sealed record BlueprintDependencyNode(string Path, bool IsInspected);
public sealed record BlueprintDependencyEdge(string Source, string Target, BlueprintDependencyKind Kind,
    BlueprintDependencyResolution Resolution, string? SourceFunction = null, string? Member = null,
    int? Offset = null, bool IsRequiredForReconstruction = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsRuntimeDispatch { get; init; }
}
public sealed record BlueprintDependencyMap(IReadOnlyList<BlueprintDependencyNode> Nodes,
    IReadOnlyList<BlueprintDependencyEdge> Edges)
{
    public int SchemaVersion => 1;
}

public sealed record BlueprintBatchReconstructionResult(int ClassesInspected, int ClassesIncluded, int ClassesExcluded,
    int FunctionsReconstructed, int FunctionsStubbed, int FunctionsOmitted, int Omissions, bool ScanIncomplete)
{
    public int EventsReconstructed { get; init; }
    public int ComponentsReconstructed { get; init; }
    public int ComponentOverridesReconstructed { get; init; }
    public bool HasGaps => ScanIncomplete || ClassesIncluded == 0 || ClassesExcluded > 0 || FunctionsStubbed > 0 ||
        FunctionsOmitted > 0 || Omissions > 0;
}

public static partial class BlueprintRecovery
{
    internal const int MaxLinkedBlueprints = 128;
    private const string UnknownDependency = "<unresolved>";
    private static readonly StringComparer BlueprintPathComparer = StringComparer.OrdinalIgnoreCase;

    public static void WriteDependencyMap(BlueprintRecoveryReport report, string path) =>
        WriteJson(report.DependencyMap, path);

    public static void WriteLinkedReconstruction(BlueprintRecoveryReport report, string rootClass, string path,
        CancellationToken cancellationToken = default) =>
        WriteJson(CreateLinkedReconstruction(report.Classes, rootClass, cancellationToken), path);

    public static BlueprintBatchReconstructionResult WriteBatchReconstruction(BlueprintRecoveryReport report, string path,
        CancellationToken cancellationToken = default) => WriteBatchPlan(report, path, cancellationToken).Coverage!.Summary;

    internal static BlueprintLinkedReconstructionPlan WriteBatchPlan(BlueprintRecoveryReport report, string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var plan = CreateBatchReconstruction(report, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        WriteJson(plan, path);
        return plan;
    }

    internal static BlueprintLinkedReconstructionPlan CreateBatchReconstruction(BlueprintRecoveryReport report,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var unique = IndexClasses(report.Classes);
        var candidates = new List<(BlueprintRecoveryClass Source, string Name)>();
        var excluded = new List<BlueprintBatchExcludedClass>();
        foreach (var item in report.Classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!unique.ContainsKey(item.Path))
                Exclude(item, BlueprintBatchExclusion.AmbiguousSourcePath, "Several inspected classes share this source path.");
            else if (!item.MetadataComplete)
                Exclude(item, BlueprintBatchExclusion.IncompleteMetadata, "The generated class metadata is incomplete.");
            else if (item.Parent != FunctionLibraryParent && FindActorAncestors(item, unique) is null)
                Exclude(item, BlueprintBatchExclusion.UnsupportedParent, "The supported Actor parent chain is incomplete or unresolved.");
            else if (!IsIdentifier(ReconstructionName(item)))
                Exclude(item, BlueprintBatchExclusion.UnrestorableClass, "The Blueprint name cannot be recreated.");
            else candidates.Add((item, ReconstructionName(item)));
        }

        var collisions = candidates.GroupBy(candidate => candidate.Name, BlueprintPathComparer)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(BlueprintPathComparer);
        var restorable = new Dictionary<string, BlueprintRecoveryClass>(BlueprintPathComparer);
        foreach (var (source, name) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (collisions.Contains(name))
                Exclude(source, BlueprintBatchExclusion.DuplicateDestinationName, $"Several classes would create the asset {name} in the same folder.");
            else restorable.Add(source.Path, source);
        }
        foreach (var item in restorable.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Parent == FunctionLibraryParent || FindActorAncestors(item, restorable) is not null) continue;
            restorable.Remove(item.Path);
            Exclude(item, BlueprintBatchExclusion.ParentExcluded, "A Blueprint needed as a parent is excluded from this plan.");
        }

        // Rebuild with only unique eligible targets. A call to an excluded class stays a stub.
        var plans = new Dictionary<string, BlueprintReconstructionPlan>(BlueprintPathComparer);
        foreach (var item in restorable.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plans.Add(item.Path, CreateReconstruction(item, restorable));
        }
        var ordered = OrderBatchPlans(plans, cancellationToken);
        var reachable = ordered.Select(plan => plan.SourceAsset).ToHashSet(BlueprintPathComparer);
        foreach (var item in restorable.Values)
            if (!reachable.Contains(item.Path))
            Exclude(item, BlueprintBatchExclusion.DependencyCycle, "The class depends on a cycle of reconstruction dependencies.");
        var stubs = ordered.SelectMany(plan => plan.Functions.Where(function => function.Body.Kind == "stub")
            .Select(function => new BlueprintBatchStub(plan.SourceAsset, function.Name, function.Body.Reason!))).ToArray();
        var scan = new BlueprintBatchScanCoverage(report.PackagesSelected, report.PackagesInspected, report.PackagesSkipped,
            report.PackagesFailed, report.LockedContainers, report.PackageLimitReached, report.InstructionLimitReached,
            report.MetadataLimitReached, report.IncompleteClasses, report.IncompleteFunctions, report.DiagnosticsOmitted,
            report.Diagnostics);
        var summary = new BlueprintBatchReconstructionResult(report.Classes.Count, ordered.Count, excluded.Count,
            ordered.Sum(item => item.Functions.Count) - stubs.Length, stubs.Length,
            ordered.Sum(item => restorable[item.SourceAsset].Functions.Count - item.Functions.Count -
                                (item.Events?.Count ?? 0) - item.RecreatedCompilerFunctions),
            ordered.Sum(item => item.Omissions.Count), scan.IsIncomplete)
        {
            EventsReconstructed = ordered.Sum(item => item.Events?.Count ?? 0),
            ComponentsReconstructed = ordered.Sum(item => item.Components?.Count ?? 0),
            ComponentOverridesReconstructed = ordered.Sum(item => item.ComponentOverrides?.Count ?? 0)
        };
        return new BlueprintLinkedReconstructionPlan(2, ordered)
        {
            Coverage = new BlueprintBatchCoverage(summary, scan, excluded.OrderBy(item => item.ClassPath, BlueprintPathComparer)
                .ThenBy(item => item.ClassPath, StringComparer.Ordinal).ToArray(), stubs)
        };

        void Exclude(BlueprintRecoveryClass item, BlueprintBatchExclusion reason, string message) =>
            excluded.Add(new BlueprintBatchExcludedClass(item.Path, item.Name, reason, message));
    }

    private static List<BlueprintReconstructionPlan> OrderBatchPlans(IReadOnlyDictionary<string, BlueprintReconstructionPlan> plans,
        CancellationToken cancellationToken)
    {
        var dependents = plans.Keys.ToDictionary(path => path, _ => new List<string>(), BlueprintPathComparer);
        var outstanding = new Dictionary<string, int>(BlueprintPathComparer);
        var ready = new SortedSet<string>(BlueprintPathComparer);
        foreach (var (path, plan) in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var required = 0;
            foreach (var dependency in PlanDependencies(plan))
            {
                dependents[dependency].Add(path);
                required++;
            }
            outstanding.Add(path, required);
            if (required == 0) ready.Add(path);
        }

        // Choose the next independent class by path and release callers only once every
        // required class is in the plan.
        var ordered = new List<BlueprintReconstructionPlan>(plans.Count);
        while (ready.Min is { } path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ready.Remove(path);
            ordered.Add(plans[path]);
            foreach (var dependent in dependents[path])
                if (--outstanding[dependent] == 0) ready.Add(dependent);
        }
        return ordered;
    }

    internal static BlueprintLinkedReconstructionPlan CreateLinkedReconstruction(
        IReadOnlyList<BlueprintRecoveryClass> classes, string rootClass, CancellationToken cancellationToken = default)
    {
        var classesByPath = IndexClasses(classes);
        if (!classesByPath.TryGetValue(rootClass, out var root))
            throw new UserFacingException("The exact root Blueprint path was absent or ambiguous in the inspection report.",
                "Select a unique class path from the report.");

        var ordered = new List<BlueprintReconstructionPlan>();
        var visited = new HashSet<string>(BlueprintPathComparer);
        var visiting = new HashSet<string>(BlueprintPathComparer);
        var names = new HashSet<string>(BlueprintPathComparer);
        Visit(root);
        return new BlueprintLinkedReconstructionPlan(2, ordered);

        void Visit(BlueprintRecoveryClass item)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited.Contains(item.Path)) return;
            if (!visiting.Add(item.Path))
                throw new UserFacingException($"The linked Blueprint plans contain a cycle at {item.Path}.");
            if (visited.Count + visiting.Count > MaxLinkedBlueprints)
                throw new UserFacingException($"The linked Blueprint plan exceeds {MaxLinkedBlueprints} classes.");

            var plan = CreateReconstruction(item, classesByPath);
            if (!names.Add(plan.Name))
                throw new UserFacingException($"Several linked classes would use the same output asset name ({plan.Name}).",
                    "Restore the classes individually to separate folders.");
            foreach (var dependency in PlanDependencies(plan).Order(BlueprintPathComparer))
                Visit(classesByPath[dependency]);

            visiting.Remove(item.Path);
            visited.Add(item.Path);
            ordered.Add(plan);
        }
    }

    internal static BlueprintDependencyMap BuildDependencyMap(IReadOnlyList<BlueprintRecoveryClass> classes,
        CancellationToken cancellationToken)
    {
        var classesByPath = IndexClasses(classes);
        var nodes = new Dictionary<string, BlueprintDependencyNode>(BlueprintPathComparer);
        foreach (var item in classes)
            nodes.TryAdd(item.Path, new BlueprintDependencyNode(item.Path, true));

        var edges = new List<BlueprintDependencyEdge>();
        foreach (var item in classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!classesByPath.ContainsKey(item.Path)) continue;
            Add(item.Path, item.Parent, BlueprintDependencyKind.Parent,
                required: item.MetadataComplete && IsIdentifier(ReconstructionName(item)) &&
                    FindActorAncestors(item, classesByPath) is { Count: > 0 });
            foreach (var component in item.Components)
                if (component.StaticMesh is { } mesh)
                    Add(item.Path, mesh, BlueprintDependencyKind.Asset, member: component.Name);
            foreach (var component in item.ComponentOverrides)
                if (component.StaticMesh is { } overrideMesh)
                    Add(item.Path, overrideMesh, BlueprintDependencyKind.Asset, member: component.Name);
            foreach (var variable in item.Variables)
                if (variable.TypeReference is { } type)
                    Add(item.Path, type, BlueprintDependencyKind.Type, member: variable.Name);

            foreach (var function in item.Functions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var parameter in function.Parameters)
                    if (parameter.TypeReference is { } type)
                        Add(item.Path, type, BlueprintDependencyKind.Type, function.Name, parameter.Name);
                foreach (var instruction in function.Instructions)
                    foreach (var (call, owner, runtimeDispatch) in EnumerateCalls(instruction, function, cancellationToken))
                        Add(item.Path, owner, BlueprintDependencyKind.Call, function.Name,
                            call.ReferenceName ?? call.Reference, call.Offset, runtimeDispatch);
            }
        }

        var requiredCalls = new Dictionary<string, HashSet<(string Function, string Target, string Member)>>(BlueprintPathComparer);
        for (var index = 0; index < edges.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edge = edges[index];
            if (edge.Kind != BlueprintDependencyKind.Call || edge.Resolution != BlueprintDependencyResolution.Inspected ||
                BlueprintPathComparer.Equals(edge.Source, edge.Target) || edge.SourceFunction is not { } function ||
                edge.Member is not { } member) continue;
            if (!requiredCalls.TryGetValue(edge.Source, out var calls))
                requiredCalls.Add(edge.Source, calls = PlannedCalls(classesByPath[edge.Source], classesByPath));
            if (calls.Contains((function, edge.Target, member)))
                edges[index] = edge with { IsRequiredForReconstruction = true };
        }
        return new BlueprintDependencyMap(nodes.Values.OrderBy(node => node.Path, BlueprintPathComparer).ToArray(), edges);

        void Add(string source, string? target, BlueprintDependencyKind kind, string? function = null,
            string? member = null, int? offset = null, bool isRuntimeDispatch = false, bool required = false)
        {
            var resolution = BlueprintDependencyResolution.Unknown;
            if (target is null) target = UnknownDependency;
            else if (classesByPath.TryGetValue(target, out var inspected))
            {
                resolution = BlueprintDependencyResolution.Inspected;
                target = inspected.Path;
            }
            else if (!nodes.TryGetValue(target, out var known) || !known.IsInspected)
                resolution = target.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)
                    ? BlueprintDependencyResolution.Native : BlueprintDependencyResolution.OutsideReport;
            nodes.TryAdd(target, new BlueprintDependencyNode(target, false));
            edges.Add(new BlueprintDependencyEdge(source, target, kind, resolution, function, member, offset,
                IsRequiredForReconstruction: required && resolution == BlueprintDependencyResolution.Inspected)
            {
                IsRuntimeDispatch = isRuntimeDispatch
            });
        }
    }

    private static HashSet<(string Function, string Target, string Member)> PlannedCalls(BlueprintRecoveryClass source,
        IReadOnlyDictionary<string, BlueprintRecoveryClass> classesByPath)
    {
        try
        {
            return CreateReconstruction(source, classesByPath).Functions
                .Where(function => function.Body.TargetClass is not null && function.Body.TargetFunction is not null)
                .Select(function => (function.Name, function.Body.TargetClass!, function.Body.TargetFunction!))
                .ToHashSet();
        }
        catch (UserFacingException)
        {
            return [];
        }
    }

    private static Dictionary<string, BlueprintRecoveryClass> IndexClasses(IReadOnlyList<BlueprintRecoveryClass> classes) =>
        classes.GroupBy(item => item.Path, BlueprintPathComparer).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), BlueprintPathComparer);

    private static IEnumerable<string> PlanDependencies(BlueprintReconstructionPlan plan) =>
        plan.Functions.SelectMany(function => function.Inputs.Concat(function.Outputs).Select(pin => pin.TypeReference)
            .Append(function.Body.TargetClass))
            .Append(plan.ParentClass is ActorParent or FunctionLibraryParent ? null : plan.ParentClass)
            .OfType<string>().Distinct(BlueprintPathComparer);

    private static IEnumerable<(BlueprintRecoveryInstruction Call, string? Owner, bool RuntimeDispatch)> EnumerateCalls(
        BlueprintRecoveryInstruction instruction, BlueprintRecoveryFunction function, CancellationToken cancellationToken,
        (string? Owner, bool RuntimeDispatch) context = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (instruction.Opcode is nameof(EExprToken.EX_FinalFunction) or nameof(EExprToken.EX_LocalFinalFunction) or
            nameof(EExprToken.EX_CallMath) or nameof(EExprToken.EX_CallMulticastDelegate))
            yield return (instruction, instruction.ReferenceOwner, false);
        if (instruction.Opcode is nameof(EExprToken.EX_VirtualFunction) or nameof(EExprToken.EX_LocalVirtualFunction))
            yield return (instruction, context.Owner, context.RuntimeDispatch);
        var member = instruction is { Opcode: nameof(EExprToken.EX_Context) or nameof(EExprToken.EX_ClassContext),
            Operands: [var receiver, var nested] } ? (Instruction: nested, Context: ResolveCallContext(receiver, function)) : default;
        foreach (var operand in instruction.Operands)
            foreach (var call in EnumerateCalls(operand, function, cancellationToken, ReferenceEquals(operand, member.Instruction) ? member.Context : default))
                yield return call;
    }

    private static (string? Owner, bool RuntimeDispatch) ResolveCallContext(BlueprintRecoveryInstruction receiver,
        BlueprintRecoveryFunction function)
    {
        if (receiver is { Opcode: nameof(EExprToken.EX_ObjectConst), ReferenceKind: BlueprintRecoveryScan.BlueprintDefaultObjectImport })
            return (receiver.ReferenceOwner, false);
        if (receiver is { Opcode: nameof(EExprToken.EX_LocalVariable), Operands.Count: 0, Reference: { } variable } &&
            receiver.ReferenceOwner == function.Path)
        {
            var type = function.Parameters.FirstOrDefault(parameter => parameter.Direction == BlueprintParameterDirection.Input &&
                parameter.Type == "ObjectProperty" && parameter.ArrayDimension == 1 && parameter.Name == variable)?.TypeReference;
            if (type is not null) return (type, true);
        }
        return default;
    }
}

internal sealed record BlueprintLinkedReconstructionPlan(int SchemaVersion, IReadOnlyList<BlueprintReconstructionPlan> Blueprints)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlueprintBatchCoverage? Coverage { get; init; }
}

internal enum BlueprintBatchExclusion
{
    AmbiguousSourcePath, UnsupportedParent, IncompleteMetadata, UnrestorableClass, DuplicateDestinationName, DependencyCycle,
    ParentExcluded
}

internal sealed record BlueprintBatchExcludedClass(string ClassPath, string Name, BlueprintBatchExclusion Reason, string Message);
internal sealed record BlueprintBatchStub(string ClassPath, string Function, string Reason);
internal sealed record BlueprintBatchScanCoverage(int PackagesSelected, int PackagesInspected, int PackagesSkipped, int PackagesFailed,
    int LockedContainers, bool PackageLimitReached, bool InstructionLimitReached, bool MetadataLimitReached,
    int IncompleteClasses, int IncompleteFunctions, int DiagnosticsOmitted, IReadOnlyList<BlueprintRecoveryDiagnostic> Diagnostics)
{
    public bool IsIncomplete => PackagesSelected != PackagesInspected || PackagesSkipped > 0 || PackagesFailed > 0 || LockedContainers > 0 ||
        PackageLimitReached || InstructionLimitReached || MetadataLimitReached || IncompleteClasses > 0 || IncompleteFunctions > 0 ||
        DiagnosticsOmitted > 0 || Diagnostics.Count > 0;
}
internal sealed record BlueprintBatchCoverage(BlueprintBatchReconstructionResult Summary, BlueprintBatchScanCoverage Scan,
    IReadOnlyList<BlueprintBatchExcludedClass> ExcludedClasses, IReadOnlyList<BlueprintBatchStub> Stubs);
