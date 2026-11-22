using System.Text.Json.Serialization;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace UEBulkExport;

public sealed record BlueprintComponentVector(double X, double Y, double Z);
public sealed record BlueprintComponentRotation(double Pitch, double Yaw, double Roll);
public sealed record BlueprintRecoveryComponent(string Name, string? ClassPath, string? Parent,
    bool IsDefaultSceneRoot, string? TemplatePath, string? Socket = null, string? ExternalParent = null,
    string? ExternalOwner = null, BlueprintComponentVector? RelativeLocation = null,
    BlueprintComponentRotation? RelativeRotation = null, BlueprintComponentVector? RelativeScale = null,
    bool? Visible = null, bool? HiddenInGame = null, string? BlockingIssue = null,
    string? StaticMesh = null, string? VariableGuid = null);

internal sealed record BlueprintReconstructionComponent(string Name, string ClassPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Parent, bool IsDefaultSceneRoot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BlueprintComponentVector? RelativeLocation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BlueprintComponentRotation? RelativeRotation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BlueprintComponentVector? RelativeScale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Visible,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HiddenInGame,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StaticMesh = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Socket = null);

public static partial class BlueprintRecovery
{
    internal const string SceneComponentClass = "/Script/Engine.SceneComponent";
    internal const string StaticMeshComponentClass = "/Script/Engine.StaticMeshComponent";

    internal static bool IsStaticMeshObjectPath(string path)
    {
        const string game = "/Game/";
        const string engine = "/Engine/";
        var mountLength = path.StartsWith(game, StringComparison.Ordinal) ? game.Length :
            path.StartsWith(engine, StringComparison.Ordinal) ? engine.Length : 0;
        var separator = path.IndexOf('.');
        return path.Length <= 1024 && mountLength > 0 && separator > mountLength &&
               IsAssetSegment(path[(separator + 1)..]) && path[mountLength..separator].Split('/').All(IsAssetSegment);

        static bool IsAssetSegment(string name) => name.Length is > 0 and <= 128 &&
            name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static bool IsComponentVariable(BlueprintRecoveryClass source, BlueprintRecoveryVariable variable) =>
        variable is { Type: "ObjectProperty", ArrayDimension: 1, TypeReference: not null } &&
        source.Components.Any(component => component.Name == variable.Name && component.ClassPath == variable.TypeReference);

    private static List<BlueprintReconstructionComponent> ProjectComponents(BlueprintRecoveryClass source,
        HashSet<string> members, List<string> omissions)
    {
        omissions.AddRange(source.ComponentIssues);
        var projected = new List<BlueprintReconstructionComponent>();
        if (source.Components.Count == 0) return projected;
        var names = source.Components.GroupBy(component => component.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var rootCount = source.Components.Count(component => component.Parent is null);
        var available = new Dictionary<string, BlueprintReconstructionComponent>(StringComparer.Ordinal);
        foreach (var component in source.Components)
        {
            var reason = component.BlockingIssue;
            if (reason is null)
            {
                if (source.Parent != ActorParent)
                    reason = "local scene tree reconstruction requires a direct Actor parent.";
                else if (component.ClassPath is not (SceneComponentClass or StaticMeshComponentClass))
                    reason = $"unsupported component class {component.ClassPath ?? "<unresolved>"}.";
                else if (!IsIdentifier(component.Name) || names[component.Name] != 1 ||
                         members.Contains(component.Name))
                    reason = "component name is invalid, ambiguous or already used.";
                else if (component.ExternalParent is not null || component.ExternalOwner is not null)
                    reason = "native and inherited attachments are outside the supported subset.";
                else if (component.Parent is null ? rootCount != 1 : !available.ContainsKey(component.Parent))
                    reason = "the local scene root or attachment parent could not be reconstructed.";
                else if (component.IsDefaultSceneRoot && (component.Parent is not null || component.Name != "DefaultSceneRoot" ||
                                                         component.ClassPath != SceneComponentClass))
                    reason = "the declared default root does not have the expected name or attachment.";
                else if (component.Socket is { } socket &&
                         (!IsIdentifier(socket) || component.Parent is not { } parent ||
                          available[parent] is not { ClassPath: StaticMeshComponentClass, StaticMesh: not null }))
                    reason = "the socket does not have a supported name and a local StaticMeshComponent with an identified mesh.";
            }
            if (reason is not null)
            {
                omissions.Add($"Component {component.Name}: {reason}");
                continue;
            }
            var staticMesh = component.StaticMesh;
            if (staticMesh is not null && (component.ClassPath != StaticMeshComponentClass || !IsStaticMeshObjectPath(staticMesh)))
            {
                omissions.Add($"Component {component.Name}: the StaticMesh object path could not be reproduced.");
                staticMesh = null;
            }
            members.Add(component.Name);
            var plan = new BlueprintReconstructionComponent(component.Name, component.ClassPath!, component.Parent,
                component.IsDefaultSceneRoot, component.RelativeLocation, component.RelativeRotation, component.RelativeScale,
                component.Visible, component.HiddenInGame, staticMesh, component.Socket);
            available.Add(component.Name, plan);
            projected.Add(plan);
        }
        return projected;
    }
}

internal sealed partial class BlueprintRecoveryScan
{
    private const int MaxComponentsPerClass = 128;
    private const int MaxComponentReferences = 512;
    private const int MaxComponentIssues = 64;

    private IReadOnlyList<BlueprintRecoveryComponent> ReadComponents(UBlueprintGeneratedClass generated,
        ResolvedObject classExport, out IReadOnlyList<string> componentIssues)
    {
        var issues = new List<string>();
        componentIssues = issues;
        if (generated.ComponentTemplates.Any(template => template is { IsNull: false }))
            AddComponentIssue(issues, "Dynamically created component templates are outside the supported subset.");
        if (generated.SimpleConstructionScript is not { IsNull: false } reference) return [];

        var components = new List<(int Index, BlueprintRecoveryComponent Item)>();
        var visited = new HashSet<int>();
        var repeated = new HashSet<int>();
        try
        {
            if (ReadLocalComponentExport(reference, classExport, "SimpleConstructionScript") is not { } scriptExport ||
                scriptExport.Load<USimpleConstructionScript>() is not { } script)
            {
                AddComponentIssue(issues, "The SimpleConstructionScript reference could not be read as a local bounded export.");
                return [];
            }

            var defaultIndex = script.DefaultSceneRootNode?.Index;
            var pending = new Queue<(FPackageIndex Reference, string? Parent, int Depth)>();
            var referencesSeen = 0;
            var referenceLimitReached = false;
            Enqueue(script.RootNodes, null, 0);
            while (pending.TryDequeue(out var node))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(node.Reference.Index))
                {
                    repeated.Add(node.Reference.Index);
                    continue;
                }
                if (node.Depth >= MaxObjectDepth || visited.Count > MaxComponentsPerClass || !TakeMetadata())
                {
                    _metadataLimit = true;
                    AddComponentIssue(issues, "Component traversal reached its metadata, node count or depth limit.");
                    break;
                }
                try
                {
                    if (ReadLocalComponentExport(node.Reference, scriptExport, "SCS_Node") is not { } nodeExport ||
                        nodeExport.Load<USCS_Node>() is not { } value)
                    {
                        AddComponentIssue(issues, $"SCS node {node.Reference.Index} could not be read as a local bounded export.");
                        continue;
                    }
                    var name = ComponentName(value.InternalVariableName) ??
                        ComponentName(value.GetOrDefault<FName>("VariableName")) ?? $"<unnamed:{node.Reference.Index}>";
                    var evidence = ReadComponent(value, classExport, name, node.Parent, defaultIndex == node.Reference.Index, issues);
                    components.Add((node.Reference.Index, evidence));
                    Enqueue(value.ChildNodes, name, node.Depth + 1);
                }
                catch (Exception error) when (IsReadFailure(error))
                {
                    AddComponentIssue(issues, $"SCS node {node.Reference.Index}: {Clip(error.Message)}");
                }
            }
            if (script.AllNodes.Any(item => item is { IsNull: false } && item.Index != defaultIndex && !visited.Contains(item.Index)))
                AddComponentIssue(issues, "The construction script lists nodes outside its active root tree.");

            void Enqueue(FPackageIndex?[] nodes, string? parent, int depth)
            {
                if (referenceLimitReached) return;
                foreach (var node in nodes)
                {
                    if (referencesSeen == MaxComponentReferences)
                    {
                        referenceLimitReached = true;
                        _metadataLimit = true;
                        AddComponentIssue(issues, "Component traversal reached its reference count limit.");
                        return;
                    }
                    referencesSeen++;
                    if (node is not { IsNull: false })
                    {
                        AddComponentIssue(issues, "The construction script contains an empty node reference.");
                        continue;
                    }
                    pending.Enqueue((node, parent, depth));
                }
            }
        }
        catch (Exception error) when (IsReadFailure(error))
        {
            AddComponentIssue(issues, $"SimpleConstructionScript: {Clip(error.Message)}");
        }
        return components.Select(component => repeated.Contains(component.Index)
            ? component.Item with { BlockingIssue = "the node has several attachment paths or forms a cycle." }
            : component.Item).ToArray();
    }

    private static BlueprintRecoveryComponent ReadComponent(USCS_Node node, ResolvedObject classExport,
        string name, string? parent, bool isDefault, List<string> issues)
    {
        var componentClass = ObjectPath(node.GetOrDefault<FPackageIndex?>("ComponentClass")?.ResolvedObject);
        var socket = ComponentName(node.GetOrDefault<FName>("AttachToName"));
        var externalParent = ComponentName(node.GetOrDefault<FName>("ParentComponentOrVariableName"));
        var externalOwner = ComponentName(node.GetOrDefault<FName>("ParentComponentOwnerClassName"));
        var templateRef = node.ComponentTemplate;
        var evidence = new BlueprintRecoveryComponent(name, componentClass, parent, isDefault,
            ObjectPath(templateRef?.ResolvedObject), socket, externalParent, externalOwner,
            VariableGuid: node.VariableGuid.IsValid() ? node.VariableGuid.ToString() : null);
        if (node.GetOrDefault<bool>("bIsParentComponentNative"))
            return evidence with { BlockingIssue = "the component is attached to a native parent." };
        var templateClass = componentClass switch
        {
            BlueprintRecovery.SceneComponentClass => "SceneComponent",
            BlueprintRecovery.StaticMeshComponentClass => "StaticMeshComponent",
            _ => null
        };
        if (templateClass is null) return evidence;
        if (ReadLocalComponentExport(templateRef, classExport, templateClass) is not { } templateExport ||
            ObjectPath(templateExport.Class) != componentClass ||
            templateExport.Load<USceneComponent>() is not { } template)
            return evidence with { BlockingIssue = $"the matching local {templateClass} template is unavailable." };

        string? staticMesh = null;
        if (componentClass == BlueprintRecovery.StaticMeshComponentClass &&
            !TryReadStaticMeshReference(template, out staticMesh) &&
            template.Properties.Any(tag => tag.Name.Text == "StaticMesh"))
            AddComponentIssue(issues, $"Component {name}: the serialized StaticMesh was not identified as a top-level /Game or /Engine asset.");
        if (template is UStaticMeshComponent mesh &&
            (mesh.LODData is { Length: > 0 } || mesh.MeshPaintTextureCooked is { IsNull: false }))
            AddComponentIssue(issues, $"Component {name}: cooked LOD overrides or mesh painting are outside the supported subset.");

        foreach (var tag in template.Properties)
        {
            var property = tag.Name.Text;
            if (property is "RelativeLocation" or "RelativeRotation" or "RelativeScale3D" or "bVisible" or "bHiddenInGame" or
                "bAbsoluteLocation" or "bAbsoluteRotation" or "bAbsoluteScale")
                continue;
            if (property == "StaticMesh" && componentClass == BlueprintRecovery.StaticMeshComponentClass) continue;
            if (property == "AttachSocketName" && socket is not null &&
                ComponentName(template.GetOrDefault<FName>("AttachSocketName")) == socket)
                continue;
            AddComponentIssue(issues, $"Component {name}: serialized template property {Clip(property)} is outside the supported subset.");
        }

        return evidence with
        {
            RelativeLocation = ReadComponentProperty<FVector, BlueprintComponentVector>(template, "RelativeLocation", Vector, name, issues),
            RelativeRotation = ReadComponentProperty<FRotator, BlueprintComponentRotation>(template, "RelativeRotation", Rotation, name, issues),
            RelativeScale = ReadComponentProperty<FVector, BlueprintComponentVector>(template, "RelativeScale3D", Vector, name, issues),
            Visible = ReadComponentProperty<bool, bool?>(template, "bVisible", value => value, name, issues),
            HiddenInGame = ReadComponentProperty<bool, bool?>(template, "bHiddenInGame", value => value, name, issues),
            StaticMesh = staticMesh,
            BlockingIssue = template.GetOrDefault<bool>("bAbsoluteLocation") || template.GetOrDefault<bool>("bAbsoluteRotation") ||
                            template.GetOrDefault<bool>("bAbsoluteScale")
                ? "absolute transform flags are outside the supported subset." : null
        };
    }

    private static bool TryReadStaticMeshReference(UObject template, out string? staticMesh)
    {
        staticMesh = null;
        if (!template.TryGetValue(out FPackageIndex? reference, "StaticMesh")) return false;
        if (reference is null || reference.IsNull) return true;

        // Legacy PAK imports carry the long package name. A resolved package's Name may
        // be the archive extraction path instead, even when the mesh successfully mounts.
        staticMesh = ReadTopLevelAssetPath(reference, static type => type == "/Script/Engine.StaticMesh");
        return staticMesh is not null;
    }

    internal static string? ReadTopLevelAssetPath(FPackageIndex reference, Func<string?, bool> supportsClass)
    {
        var path = reference is { IsImport: true, Owner: Package legacy }
            ? ReadLegacyTopLevelAssetPath(legacy, reference, supportsClass)
            : reference.ResolvedObject is { ExportIndex: >= 0, Outer: ResolvedPackageObject owner } asset &&
              supportsClass(ObjectPath(asset.Class)) ? $"{owner.Name.Text}.{asset.Name.Text}" : null;
        return path is not null && BlueprintRecovery.IsStaticMeshObjectPath(path) ? path : null;
    }

    private static string? ReadLegacyTopLevelAssetPath(Package package, FPackageIndex reference,
        Func<string?, bool> supportsClass)
    {
        var index = -1L - reference.Index;
        if (index < 0 || index >= package.ImportMap.Length) return null;
        var asset = package.ImportMap[(int)index];
        var outerIndex = asset.OuterIndex is { IsImport: true } outer ? -1L - outer.Index : -1;
        if (!supportsClass($"{asset.ClassPackage.Text}.{asset.ClassName.Text}") ||
            outerIndex < 0 || outerIndex >= package.ImportMap.Length)
            return null;
        var owner = package.ImportMap[(int)outerIndex];
        // Tagged UE 5.8 imports can repeat the short object name in this optional field.
        // The separate root Package import still carries the full Unreal path.
        var packageName = asset.PackageName.Text;
        return owner.ClassPackage.Text == "/Script/CoreUObject" && owner.ClassName.Text == "Package" &&
               owner.OuterIndex is { IsNull: true } &&
               (asset.PackageName.IsNone || packageName == asset.ObjectName.Text || packageName == owner.ObjectName.Text)
            ? $"{owner.ObjectName.Text}.{asset.ObjectName.Text}" : null;
    }

    private static TDto? ReadComponentProperty<TValue, TDto>(UObject template, string property,
        Func<TValue, TDto?> convert, string name, List<string> issues)
    {
        if (template.TryGetValue(out TValue value, property) && convert(value) is { } converted)
            return converted;
        if (template.Properties.Any(tag => tag.Name.Text == property))
            AddComponentIssue(issues, $"Component {name}: {property} was serialized but could not be represented.");
        return default;
    }

    private static BlueprintComponentVector? Vector(FVector value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z)
            ? new(value.X, value.Y, value.Z) : null;

    private static BlueprintComponentRotation? Rotation(FRotator value) =>
        double.IsFinite(value.Pitch) && double.IsFinite(value.Yaw) && double.IsFinite(value.Roll)
            ? new(value.Pitch, value.Yaw, value.Roll) : null;

    private static ResolvedObject? ReadLocalComponentExport(FPackageIndex? reference, ResolvedObject outer, string className) =>
        reference is { IsExport: true, ResolvedObject: { Outer: { } owner } export } &&
        owner.ExportIndex == outer.ExportIndex && ReferenceEquals(owner.Package, outer.Package) &&
        ReferenceEquals(export.Package, outer.Package) && export.Class?.Name.Text == className &&
        IsWithinExportSize(export) ? export : null;

    private static string? ComponentName(FName name) => name.IsNone ? null : Clip(name.Text);

    private static void AddComponentIssue(List<string> issues, string issue)
    {
        if (issues.Count < MaxComponentIssues) issues.Add(issue);
        else if (issues.Count == MaxComponentIssues) issues.Add("Additional component issues were omitted.");
    }
}
