using System.Buffers.Binary;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace UEBulkExport.Tests;

public sealed class BlueprintMeshRecoveryTests
{
    [Theory]
    [InlineData("/Game/Furniture/Mesh", "ProjectA/Content/Furniture/Mesh.uasset")]
    [InlineData("/Game/Content/Mesh", "ProjectA/Content/Content/Mesh.uasset")]
    [InlineData("/Game/Plugins/Mesh", "ProjectA/Content/Plugins/Mesh.uasset")]
    [InlineData("/Engine/BasicShapes/Cube", "Engine/Content/BasicShapes/Cube.uasset")]
    public void Container_lookup_expands_only_the_canonical_mount(string path, string expected) =>
        Assert.Equal(expected, BulkExporter.BlueprintMeshContainerPath(path, "ProjectA"));

    [Fact]
    public void A_binary_mesh_requires_glb_v2_and_a_complete_binary_chunk()
    {
        var geometry = new byte[36];
        "glTF"u8.CopyTo(geometry);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(8), (uint)geometry.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(12), 4);
        "JSON{}  "u8.CopyTo(geometry.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(24), 4);
        "BIN\0"u8.CopyTo(geometry.AsSpan(28));

        Assert.True(BulkExporter.IsBinaryGltf(geometry));
        Assert.False(BulkExporter.IsBinaryGltf(geometry.AsSpan(0, 32)));
        geometry[28] = 0;
        Assert.False(BulkExporter.IsBinaryGltf(geometry));
    }

    [Theory]
    [InlineData("PinSocket", true)]
    [InlineData("Left Hand Socket", true)]
    [InlineData("None", false)]
    [InlineData("  ", false)]
    [InlineData("Socket\n", false)]
    public void Socket_names_preserve_unreal_text_without_empty_or_control_values(string name, bool supported) =>
        Assert.Equal(supported, BlueprintRecoveryScan.IsRestorableStaticMeshSocketName(name));

    [Fact]
    public void Box_preserves_the_serialized_center_rotation_and_full_dimensions()
    {
        var source = Shape(("Center", new FVector(8, -7, 9)), ("Rotation", new FRotator(3, 15, 2)),
            ("X", 12f), ("Y", 14f), ("Z", 16f));

        Assert.Equal(new BlueprintMeshBox(new(8, -7, 9), new(3, 15, 2), new(12, 14, 16)),
            BlueprintRecoveryScan.ReadBlueprintMeshBox(source));
    }

    [Fact]
    public void Shape_defaults_apply_only_to_omitted_tags()
    {
        Assert.Equal(new BlueprintMeshBox(new(0, 0, 0), new(0, 0, 0), new(1, 1, 1)),
            BlueprintRecoveryScan.ReadBlueprintMeshBox(Shape()));
        Assert.Null(BlueprintRecoveryScan.ReadBlueprintMeshBox(Shape(("Y", null))));
        Assert.Null(BlueprintRecoveryScan.ReadBlueprintMeshSphere(Shape(("Radius", null))));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Nonpositive_or_nonfinite_dimensions_are_rejected(float value)
    {
        Assert.Null(BlueprintRecoveryScan.ReadBlueprintMeshBox(Shape(("Z", value))));
        Assert.Null(BlueprintRecoveryScan.ReadBlueprintMeshSphere(Shape(("Radius", value))));
        Assert.Null(BlueprintRecoveryScan.ReadBlueprintMeshCapsule(Shape(("Radius", value))));
    }

    [Fact]
    public void Simple_shapes_reject_unsupported_physics_and_transforms()
    {
        Assert.Null(BlueprintRecoveryScan.ReadBlueprintMeshBox(Shape(("CollisionEnabled", new FName("NoCollision")))));
        Assert.Null(BlueprintRecoveryScan.ReadBlueprintMeshSphere(Shape(("RestOffset", .5f))));
        Assert.Null(BlueprintRecoveryScan.ReadBlueprintMeshCapsule(Shape(("Rotation", new FRotator(0, float.NaN, 0)))));
    }

    [Fact]
    public void Sphere_and_capsule_keep_native_radius_and_cylindrical_length()
    {
        var center = new FVector(-2, 7, 11);
        Assert.Equal(new BlueprintMeshSphere(new(-2, 7, 11), 13),
            BlueprintRecoveryScan.ReadBlueprintMeshSphere(Shape(("Center", center), ("Radius", 13f))));
        Assert.Equal(new BlueprintMeshCapsule(new(-2, 7, 11), new(7, -3, 9), 13, 0),
            BlueprintRecoveryScan.ReadBlueprintMeshCapsule(Shape(("Center", center), ("Rotation", new FRotator(7, -3, 9)),
                ("Radius", 13f), ("Length", 0f))));
        Assert.Null(BlueprintRecoveryScan.ReadBlueprintMeshCapsule(Shape(("Length", -1f))));
    }

    private static FStructFallback Shape(params (string Name, object? Value)[] fields) =>
        new(fields.Select(field => new FPropertyTag
        {
            Name = new FName(field.Name),
            Tag = field.Value is { } value ? new ShapeValueTag(value) : null,
        }).ToList());

    private sealed class ShapeValueTag(object value) : FPropertyTagType
    {
        public override object GenericValue => value;

        public override string ToString() => value.ToString() ?? string.Empty;
    }
}
