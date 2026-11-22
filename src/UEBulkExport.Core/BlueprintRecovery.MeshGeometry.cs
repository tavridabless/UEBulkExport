using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.PhysicsEngine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Formats.Meshes;
using CUE4Parse_Conversion.Options;

namespace UEBulkExport;

internal sealed record BlueprintMeshLod(int LodIndex, string GeometryPath);
internal sealed record BlueprintMeshBox(BlueprintComponentVector Center, BlueprintComponentRotation Rotation,
    BlueprintComponentVector Dimensions);
internal sealed record BlueprintMeshSphere(BlueprintComponentVector Center, float Radius);
internal sealed record BlueprintMeshCapsule(BlueprintComponentVector Center, BlueprintComponentRotation Rotation,
    float Radius, float Length);

internal sealed record BlueprintMeshCollision(IReadOnlyList<BlueprintMeshBox> Boxes, IReadOnlyList<BlueprintMeshSphere> Spheres,
    IReadOnlyList<BlueprintMeshCapsule> Capsules)
{
    public static BlueprintMeshCollision Empty { get; } = new([], [], []);
}

public sealed partial class BulkExporter
{
    private const int MaxEditorStaticMeshLods = 8;

    private static (string GeometryPath, IReadOnlyList<BlueprintMeshLod> Lods) WriteBlueprintMeshLods(
        UStaticMesh mesh, string objectName, string objectPath, string outputDirectory,
        ExportOptions options, GltfMeshFormat format, List<BlueprintMeshExtractionIssue> warnings, CancellationToken ct)
    {
        var lodCount = mesh.RenderData!.LODs!.Length;
        using var dto = OpenMesh();
        var lods = dto.LODs.ToArray();
        if (lods.Length == 0 || lods[0].IsNanite)
            throw new UserFacingException("The mesh has no readable conventional render LOD.");
        var geometry = WriteLod(lods[0]);
        var additional = new List<BlueprintMeshLod>();
        if (lods[0].SourceLodIndex != 0)
        {
            Warn($"Source LOD0 is unavailable. Native LOD0 uses source LOD{lods[0].SourceLodIndex}; additional LODs were omitted.");
            return (geometry, additional);
        }
        for (var index = 1; index < Math.Min(lodCount, MaxEditorStaticMeshLods); index++)
        {
            ct.ThrowIfCancellationRequested();
            if (index >= lods.Length || lods[index].SourceLodIndex != (uint)index)
            {
                Warn($"Source LOD{index} could not be read. This and subsequent LODs were omitted to preserve native LOD indices.");
                break;
            }
            try { additional.Add(new BlueprintMeshLod(index, WriteLod(lods[index]))); }
            catch (Exception error) when (BlueprintRecoveryScan.IsReadFailure(error))
            {
                Warn($"Source LOD{index} conversion failed: {error.Message}. This and subsequent LODs were omitted.");
                break;
            }
        }
        return (geometry, additional);

        StaticMeshDto OpenMesh()
        {
            if (lodCount <= MaxEditorStaticMeshLods)
            {
                try { return new StaticMeshDto(mesh, EMeshQuality.All); }
                catch (Exception error) when (lodCount > 1 && BlueprintRecoveryScan.IsReadFailure(error))
                {
                    Warn($"Reading the whole LOD set failed: {error.Message}. Trying the highest available LOD alone.");
                }
            }
            else Warn($"The source LOD array exceeds the editor limit of {MaxEditorStaticMeshLods}. Only the highest available LOD was recovered.");
            return new StaticMeshDto(mesh, EMeshQuality.Highest);
        }

        string WriteLod(MeshLodDto<MeshVertex> lod)
        {
            ct.ThrowIfCancellationRequested();
            if (lod.Vertices.Length == 0 || !lod.Sections.Any(section => section.NumFaces > 0))
                throw new UserFacingException("The conventional mesh LOD has no usable triangles.");
            // Convert one at a time so a later failure keeps LOD0 and only one GLB
            // needs to remain in memory during publication.
            dto.LODs.Clear();
            dto.LODs.Add(lod);
            var output = format.BuildStaticMesh(objectName, objectPath, options, dto);
            if (output is not [{ Extension: "glb" } file] || !IsBinaryGltf(file.Data))
                throw new UserFacingException("The CUE4Parse converter did not produce a self-contained glTF 2.0 binary.");
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(outputDirectory, $"mesh-{Guid.NewGuid():N}.glb");
            AtomicFile.WriteAllBytes(destination, file.Data);
            return destination;
        }

        void Warn(string message) => warnings.Add(new BlueprintMeshExtractionIssue(objectPath, message));
    }
}

internal sealed partial class BlueprintRecoveryScan
{
    private const int MaxBlueprintCollisionShapesPerKind = 512;

    internal static BlueprintMeshCollision ReadBlueprintMeshCollision(UStaticMesh mesh, ResolvedObject meshExport,
        string objectPath, List<BlueprintMeshExtractionIssue> warnings, CancellationToken ct)
    {
        if (mesh.BodySetup is not { IsNull: false } bodyReference) return BlueprintMeshCollision.Empty;
        try
        {
            var export = ReadLocalComponentExport(bodyReference, meshExport, "BodySetup");
            if (export?.Load<UBodySetup>() is not { } body)
            {
                Warn("The bounded local StaticMesh BodySetup could not be read. Simple collision was omitted.");
                return BlueprintMeshCollision.Empty;
            }
            if (body.Properties.Any(tag => tag.Name.Text == "CollisionTraceFlag") &&
                (!body.TryGetValue(out FName trace, "CollisionTraceFlag") || !IsEnumName(trace, "CTF_UseDefault")))
                Warn("The source collision trace policy is outside the supported subset. Its available simple collision geometry was kept.");

            if (!body.TryGetValue(out FStructFallback aggregate, nameof(UBodySetup.AggGeom)))
            {
                if (body.Properties.Any(tag => tag.Name.Text == nameof(UBodySetup.AggGeom)))
                    Warn("The serialized aggregate collision structure could not be read.");
                return BlueprintMeshCollision.Empty;
            }

            foreach (var tag in aggregate.Properties)
                if (tag.Name.Text is not ("BoxElems" or "SphereElems" or "SphylElems") &&
                    tag.Tag?.GenericValue is UScriptArray { Properties.Count: > 0 })
                    Warn($"Simple collision {Clip(tag.Name.Text)} is outside the supported box, sphere and capsule subset.");

            return new BlueprintMeshCollision(
                ReadCollisionShapes(aggregate, "BoxElems", "box", ReadBlueprintMeshBox, Warn, ct),
                ReadCollisionShapes(aggregate, "SphereElems", "sphere", ReadBlueprintMeshSphere, Warn, ct),
                ReadCollisionShapes(aggregate, "SphylElems", "capsule", ReadBlueprintMeshCapsule, Warn, ct));
        }
        catch (Exception error) when (IsReadFailure(error))
        {
            Warn($"Simple collision: {Clip(error.Message)}");
            return BlueprintMeshCollision.Empty;
        }

        void Warn(string message) => warnings.Add(new BlueprintMeshExtractionIssue(objectPath, message));
    }

    private static IReadOnlyList<T> ReadCollisionShapes<T>(FStructFallback aggregate, string field, string kind,
        Func<FStructFallback, T?> project, Action<string> warn, CancellationToken ct) where T : class
    {
        if (!aggregate.TryGetValue(out FStructFallback?[] elements, field))
        {
            if (aggregate.Properties.Any(tag => tag.Name.Text == field))
                warn($"The serialized simple collision {kind} array could not be read.");
            return [];
        }
        var result = new List<T>();
        for (var index = 0; index < Math.Min(elements.Length, MaxBlueprintCollisionShapesPerKind); index++)
        {
            ct.ThrowIfCancellationRequested();
            if (elements[index] is not { } element || project(element) is not { } projected)
                warn($"Simple collision {kind} {index + 1} has unreadable dimensions, transform, or unsupported physics flags.");
            else result.Add(projected);
        }
        if (elements.Length > MaxBlueprintCollisionShapesPerKind)
            warn($"The simple collision {kind} limit ({MaxBlueprintCollisionShapesPerKind}) was reached.");
        return result;
    }

    internal static BlueprintMeshBox? ReadBlueprintMeshBox(FStructFallback box)
    {
        if (!ReadSimpleShapeCenter(box, out var center) || !ReadSimpleShapeRotation(box, out var rotation) ||
            !ReadShapeField(box, "X", 1f, out var x) || !ReadShapeField(box, "Y", 1f, out var y) ||
            !ReadShapeField(box, "Z", 1f, out var z) || !Positive(x) || !Positive(y) || !Positive(z))
            return null;
        return new BlueprintMeshBox(center, rotation, new BlueprintComponentVector(x, y, z));
    }

    internal static BlueprintMeshSphere? ReadBlueprintMeshSphere(FStructFallback sphere) =>
        ReadSimpleShapeCenter(sphere, out var center) && ReadShapeField(sphere, "Radius", 1f, out var radius) && Positive(radius)
            ? new BlueprintMeshSphere(center, radius) : null;

    internal static BlueprintMeshCapsule? ReadBlueprintMeshCapsule(FStructFallback capsule) =>
        ReadSimpleShapeCenter(capsule, out var center) && ReadSimpleShapeRotation(capsule, out var rotation) &&
        ReadShapeField(capsule, "Radius", 1f, out var radius) && Positive(radius) &&
        ReadShapeField(capsule, "Length", 1f, out var length) && float.IsFinite(length) && length >= 0
            ? new BlueprintMeshCapsule(center, rotation, radius, length) : null;

    private static bool ReadSimpleShapeCenter(FStructFallback shape, out BlueprintComponentVector center)
    {
        center = null!;
        if (!ReadShapeField(shape, "Center", FVector.ZeroVector, out var vector) || Vector(vector) is not { } location ||
            !ReadShapeField(shape, "RestOffset", 0f, out var restOffset) || restOffset != 0 ||
            !ReadShapeField(shape, "bContributeToMass", true, out var contribute) || !contribute ||
            !ReadShapeField(shape, "CollisionEnabled", new FName("QueryAndPhysics"), out var enabled) || !IsEnumName(enabled, "QueryAndPhysics") ||
            !ReadShapeField(shape, "Name", new FName("None"), out var name) || !name.IsNone)
            return false;
        center = location;
        return true;
    }

    private static bool ReadSimpleShapeRotation(FStructFallback shape, out BlueprintComponentRotation rotation)
    {
        rotation = null!;
        if (!ReadShapeField(shape, "Rotation", FRotator.ZeroRotator, out var angles) || Rotation(angles) is not { } projected)
            return false;
        rotation = projected;
        return true;
    }

    private static bool ReadShapeField<T>(FStructFallback shape, string name, T defaultValue, out T value) where T : struct
    {
        // Native struct defaults apply to omitted tags. An unreadable serialized
        // value is different and cannot be replaced with a default.
        var property = shape.Properties.FirstOrDefault(item => item.Name.Text == name);
        if (property is null) { value = defaultValue; return true; }
        if (property.Tag?.GetValue(typeof(T)) is T parsed) { value = parsed; return true; }
        value = default;
        return false;
    }

    private static bool Positive(float value) => float.IsFinite(value) && value > 0;

    private static bool IsEnumName(FName value, string name) => value.Text == name ||
        value.Text.EndsWith("::" + name, StringComparison.Ordinal);
}
