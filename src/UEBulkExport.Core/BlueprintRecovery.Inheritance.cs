using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Objects;

namespace UEBulkExport;

public static partial class BlueprintRecovery
{
    private static string ReconstructionName(BlueprintRecoveryClass source) =>
        source.Name.EndsWith("_C", StringComparison.Ordinal) ? source.Name[..^2] : source.Name;

    private static IReadOnlyList<BlueprintRecoveryClass>? FindActorAncestors(BlueprintRecoveryClass source,
        IReadOnlyDictionary<string, BlueprintRecoveryClass>? availableClasses)
    {
        if (source.Parent == ActorParent) return [];
        if (availableClasses is null) return null;

        var visited = new HashSet<string>(BlueprintPathComparer) { source.Path };
        var ancestors = new List<BlueprintRecoveryClass>();
        var current = source;
        while (current.Parent != ActorParent)
        {
            if (current.Parent is not { } parentPath || !visited.Add(parentPath) ||
                !availableClasses.TryGetValue(parentPath, out var parent) || !parent.MetadataComplete ||
                !IsIdentifier(ReconstructionName(parent)))
                return null;
            ancestors.Add(parent);
            current = parent;
        }
        return ancestors;
    }

    private static string? FindOverrideClass(IReadOnlyList<BlueprintRecoveryClass> ancestors,
        BlueprintRecoveryFunction function)
    {
        if (!function.Pure || !CanMatchBody(function)) return null;
        foreach (var ancestor in ancestors)
        {
            var inherited = ancestor.Functions.FirstOrDefault(member =>
                member.Name.Equals(function.Name, StringComparison.OrdinalIgnoreCase));
            if (inherited is null) continue;
            if (inherited.Name != function.Name || !inherited.Pure || !inherited.ParametersComplete ||
                !CanRecreateFlags(inherited, false) ||
                !Enum.TryParse<EFunctionFlags>(inherited.Flags, out var flags) ||
                !flags.HasFlag(EFunctionFlags.FUNC_Public | EFunctionFlags.FUNC_BlueprintCallable) ||
                (flags & EFunctionFlags.FUNC_Final) != 0 ||
                inherited.Parameters.Count != function.Parameters.Count ||
                (function.SuperFunction is { } overridden && overridden != inherited.Path))
                return null;
            return inherited.Parameters.Zip(function.Parameters).All(pair =>
                pair.First.Name == pair.Second.Name && pair.First.Type == pair.Second.Type &&
                pair.First.ArrayDimension == pair.Second.ArrayDimension && pair.First.Direction == pair.Second.Direction)
                ? ancestor.Path : null;
        }
        return null;
    }

    private static IReadOnlyList<BlueprintRecoveryInheritedDefault> PlanInheritedDefaults(BlueprintRecoveryClass source,
        IReadOnlyList<BlueprintRecoveryClass> ancestors, List<string> omissions)
    {
        var result = new List<BlueprintRecoveryInheritedDefault>();
        foreach (var inherited in source.InheritedDefaults)
        {
            var declaration = ancestors.SelectMany(ancestor => ancestor.Variables)
                .FirstOrDefault(variable => variable.Name.Equals(inherited.Name, StringComparison.OrdinalIgnoreCase));
            if (declaration is not { ArrayDimension: 1 } || GetReconstructionType(declaration.Type) != inherited.Type ||
                !IsIdentifier(declaration.Name) || !MatchesType(inherited.Value, inherited.Type))
                omissions.Add($"Inherited default {inherited.Name}: unsupported or unavailable Blueprint declaration.");
            else result.Add(inherited with { Name = declaration.Name });
        }
        return result;
    }
}

internal sealed partial class BlueprintRecoveryScan
{
    private static IReadOnlyList<BlueprintRecoveryInheritedDefault> ReadInheritedDefaults(
        Dictionary<string, FPropertyTag>? defaults, IReadOnlyCollection<PropertyMetadata> properties)
    {
        if (defaults is null) return [];
        var declared = properties.Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inherited = new List<BlueprintRecoveryInheritedDefault>();
        foreach (var (name, tag) in defaults)
        {
            if (declared.Contains(name)) continue;
            switch (tag.Tag?.GenericValue)
            {
                case bool value: inherited.Add(new BlueprintRecoveryInheritedDefault(Clip(name), "bool", value)); break;
                case int value: inherited.Add(new BlueprintRecoveryInheritedDefault(Clip(name), "int", value)); break;
            }
        }
        return inherited;
    }
}
