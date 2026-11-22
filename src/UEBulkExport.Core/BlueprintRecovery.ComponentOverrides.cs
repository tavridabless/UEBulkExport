using System.Text.Json.Serialization;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace UEBulkExport;

public sealed record BlueprintRecoveryComponentOverride(string Name, string? OwnerClass, string? ClassPath, string? TemplatePath,
    string? AssociatedGuid, BlueprintComponentVector? RelativeLocation = null, BlueprintComponentRotation? RelativeRotation = null,
    BlueprintComponentVector? RelativeScale = null, bool? Visible = null, bool? HiddenInGame = null,
    string? StaticMesh = null, bool ClearStaticMesh = false, string? BlockingIssue = null);

internal sealed record BlueprintReconstructionComponentOverride(string OwnerClass, string Name, string ClassPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BlueprintComponentVector? RelativeLocation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BlueprintComponentRotation? RelativeRotation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BlueprintComponentVector? RelativeScale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Visible,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HiddenInGame,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StaticMesh,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ClearStaticMesh);

public static partial class BlueprintRecovery
{
    private static IReadOnlyList<BlueprintReconstructionComponentOverride> ProjectComponentOverrides(BlueprintRecoveryClass source,
        IReadOnlyList<BlueprintRecoveryClass> ancestors, List<string> omissions)
    {
        omissions.AddRange(source.ComponentOverrideIssues);
        var result = new List<BlueprintReconstructionComponentOverride>();
        if (source.ComponentOverrides.Count == 0) return result;

        var duplicateTargets = source.ComponentOverrides
            .GroupBy(overrideItem => (overrideItem.OwnerClass, Name: overrideItem.Name.ToUpperInvariant()))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var owners = ancestors.ToDictionary(item => item.Path, StringComparer.Ordinal);
        var projectedOwners = new Dictionary<string, List<BlueprintReconstructionComponent>>(StringComparer.Ordinal);
        foreach (var item in source.ComponentOverrides)
        {
            var reason = item.BlockingIssue;
            owners.TryGetValue(item.OwnerClass ?? "", out var owner);
            if (reason is null && owner is null)
                reason = "the exact declaring Blueprint is outside the restored parent chain.";
            else if (reason is null && owner is not null)
            {
                var declaration = owner.Components.Where(component => component.Name == item.Name).Take(2).ToArray();
                if (duplicateTargets.Contains((item.OwnerClass, item.Name.ToUpperInvariant())))
                    reason = "several serialized records target the same inherited component.";
                else if (declaration.Length != 1 || declaration[0].ClassPath != item.ClassPath ||
                         (item.AssociatedGuid is { } recordGuid && declaration[0].VariableGuid is { } declarationGuid && recordGuid != declarationGuid))
                    reason = "the name, class or SCS variable identifier did not match the declaring Blueprint.";
                else
                {
                    if (!projectedOwners.TryGetValue(owner.Path, out var projected))
                        projectedOwners.Add(owner.Path, projected = ProjectComponents(owner, new HashSet<string>(StringComparer.OrdinalIgnoreCase), []));
                    if (!projected.Any(component => component.Name == item.Name && component.ClassPath == item.ClassPath))
                        reason = "the declaring Blueprint component could not be restored.";
                }
            }

            if (reason is null && (item.StaticMesh is { } mesh && !IsStaticMeshObjectPath(mesh) ||
                (item.StaticMesh is not null || item.ClearStaticMesh) && item.ClassPath != StaticMeshComponentClass ||
                item.StaticMesh is not null && item.ClearStaticMesh))
                reason = "the serialized static mesh change is ambiguous or unsupported.";
            if (reason is null && item.RelativeLocation is null && item.RelativeRotation is null && item.RelativeScale is null &&
                item.Visible is null && item.HiddenInGame is null && item.StaticMesh is null && !item.ClearStaticMesh)
                reason = "the template contains no supported serialized change.";
            if (reason is not null)
            {
                omissions.Add($"Inherited component {item.Name}: {reason}");
                continue;
            }
            result.Add(new BlueprintReconstructionComponentOverride(item.OwnerClass!, item.Name, item.ClassPath!,
                item.RelativeLocation, item.RelativeRotation, item.RelativeScale, item.Visible, item.HiddenInGame,
                item.StaticMesh, item.ClearStaticMesh));
        }
        return result;
    }
}

internal sealed partial class BlueprintRecoveryScan
{
    private IReadOnlyList<BlueprintRecoveryComponentOverride> ReadComponentOverrides(UBlueprintGeneratedClass generated,
        ResolvedObject classExport, out IReadOnlyList<string> componentIssues)
    {
        var issues = new List<string>();
        componentIssues = issues;
        if (generated.InheritableComponentHandler is not { IsNull: false } reference) return [];

        var result = new List<BlueprintRecoveryComponentOverride>();
        try
        {
            if (ReadLocalComponentExport(reference, classExport, "InheritableComponentHandler") is not { } handlerExport ||
                handlerExport.Load<UInheritableComponentHandler>() is not { } handler)
            {
                AddComponentIssue(issues, "Inherited component overrides: the local bounded handler export could not be read.");
                return [];
            }

            // The typed CUE4Parse key loads its owner as an object. The same decoded tags keep
            // the package index, so binding does not depend on a short loaded object's name.
            var records = handler.GetOrDefault<FStructFallback?[]>("Records");
            if (records is null)
            {
                if (handler.Properties.Any(tag => tag.Name.Text == "Records"))
                    AddComponentIssue(issues, "Inherited component overrides: the serialized record array could not be read.");
                return [];
            }
            for (var index = 0; index < records.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index >= MaxComponentsPerClass || !TakeMetadata())
                {
                    _metadataLimit = true;
                    AddComponentIssue(issues, "Inherited component override traversal reached its metadata or record count limit.");
                    break;
                }
                if (records[index] is not { } record)
                {
                    AddComponentIssue(issues, $"Inherited component override {index + 1}: the serialized record could not be read.");
                    continue;
                }
                try { result.Add(ReadComponentOverride(classExport, record, index + 1, issues)); }
                catch (Exception error) when (IsReadFailure(error))
                {
                    AddComponentIssue(issues, $"Inherited component override {index + 1}: {Clip(error.Message)}");
                }
            }
        }
        catch (Exception error) when (IsReadFailure(error))
        {
            AddComponentIssue(issues, $"Inherited component overrides: {Clip(error.Message)}");
        }
        return result;
    }

    private static BlueprintRecoveryComponentOverride ReadComponentOverride(ResolvedObject classExport, FStructFallback record,
        int index, List<string> issues)
    {
        var key = record.GetOrDefault<FStructFallback?>("ComponentKey");
        var name = key is null ? null : ComponentName(key.GetOrDefault<FName>("SCSVariableName"));
        var guid = key?.GetOrDefault<FGuid>("AssociatedGuid");
        var owner = key?.GetOrDefault<FPackageIndex?>("OwnerClass")?.ResolvedObject;
        var ownerPath = ObjectPath(owner);
        var classReference = record.GetOrDefault<FPackageIndex?>("ComponentClass");
        var componentClass = ObjectPath(classReference?.ResolvedObject);
        var templateReference = record.GetOrDefault<FPackageIndex?>("ComponentTemplate");
        var evidence = new BlueprintRecoveryComponentOverride(name ?? $"<unnamed:{index}>", ownerPath, componentClass,
            ObjectPath(templateReference?.ResolvedObject), guid is { } value && value.IsValid() ? value.ToString() : null);
        if (name is null || owner is not { ExportIndex: >= 0 } || ownerPath is null ||
            owner.Class?.Name.Text.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal) != true)
            return evidence with { BlockingIssue = "the SCS key or exact declaring Blueprint could not be resolved." };

        var exportType = templateReference?.ResolvedObject?.Class;
        componentClass ??= ObjectPath(exportType);
        evidence = evidence with { ClassPath = componentClass };
        if (componentClass is not (BlueprintRecovery.SceneComponentClass or BlueprintRecovery.StaticMeshComponentClass) ||
            ObjectPath(exportType) != componentClass)
            return evidence with { BlockingIssue = "the record and template do not identify the same supported component class." };
        if (ReadLocalComponentExport(templateReference, classExport, exportType!.Name.Text) is not { } templateExport ||
            templateExport.Load<USceneComponent>() is not { } template)
            return evidence with { BlockingIssue = "the matching local bounded override template could not be read." };

        foreach (var tag in template.Properties)
        {
            var property = tag.Name.Text;
            if (property is "RelativeLocation" or "RelativeRotation" or "RelativeScale3D" or "bVisible" or "bHiddenInGame" or
                "bAbsoluteLocation" or "bAbsoluteRotation" or "bAbsoluteScale" ||
                property == "StaticMesh" && componentClass == BlueprintRecovery.StaticMeshComponentClass)
                continue;
            AddComponentIssue(issues, $"Inherited component {name}: serialized template property {Clip(property)} is outside the supported subset.");
        }

        string? staticMesh = null;
        var clearStaticMesh = false;
        if (componentClass == BlueprintRecovery.StaticMeshComponentClass && template.Properties.Any(tag => tag.Name.Text == "StaticMesh"))
        {
            if (TryReadStaticMeshReference(template, out staticMesh)) clearStaticMesh = staticMesh is null;
            else AddComponentIssue(issues, $"Inherited component {name}: the serialized static mesh reference could not be verified.");
        }

        return evidence with
        {
            RelativeLocation = ReadComponentProperty<FVector, BlueprintComponentVector>(template, "RelativeLocation", Vector, name, issues),
            RelativeRotation = ReadComponentProperty<FRotator, BlueprintComponentRotation>(template, "RelativeRotation", Rotation, name, issues),
            RelativeScale = ReadComponentProperty<FVector, BlueprintComponentVector>(template, "RelativeScale3D", Vector, name, issues),
            Visible = ReadComponentProperty<bool, bool?>(template, "bVisible", value => value, name, issues),
            HiddenInGame = ReadComponentProperty<bool, bool?>(template, "bHiddenInGame", value => value, name, issues),
            StaticMesh = staticMesh,
            ClearStaticMesh = clearStaticMesh,
            BlockingIssue = template.GetOrDefault<bool>("bAbsoluteLocation") || template.GetOrDefault<bool>("bAbsoluteRotation") ||
                            template.GetOrDefault<bool>("bAbsoluteScale")
                ? "absolute transform flags are outside the supported subset." : null
        };
    }
}
