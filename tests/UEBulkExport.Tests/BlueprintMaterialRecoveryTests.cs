using System.Buffers.Binary;

namespace UEBulkExport.Tests;

public sealed class BlueprintMaterialRecoveryTests
{
    [Theory]
    [InlineData("BaseColor", "baseColor")]
    [InlineData("Base_Color_Texture", "baseColor")]
    [InlineData("Normal Map", "normal")]
    [InlineData("ORM", null)]
    [InlineData("Roughness Detail", null)]
    public void Texture_role_is_inferred_only_from_explicit_supported_names(string name, string? expected) =>
        Assert.Equal(expected, BulkExporter.MaterialSignalChannel(name, BulkExporter.MaterialSignalKind.Texture));

    [Fact]
    public void Scalar_and_color_parameters_have_type_specific_roles()
    {
        Assert.Equal("roughness", BulkExporter.MaterialSignalChannel("Roughness", BulkExporter.MaterialSignalKind.Scalar));
        Assert.Equal("baseColor", BulkExporter.MaterialSignalChannel("BaseColor", BulkExporter.MaterialSignalKind.Color));
        Assert.Null(BulkExporter.MaterialSignalChannel("Roughness", BulkExporter.MaterialSignalKind.Color));
        Assert.Null(BulkExporter.MaterialSignalChannel("Specular", BulkExporter.MaterialSignalKind.Texture));
    }

    [Fact]
    public void Png_header_must_match_decoded_texture_dimensions()
    {
        var bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 8);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 4);

        Assert.True(BulkExporter.HasPngDimensions(bytes, 8, 4));
        Assert.False(BulkExporter.HasPngDimensions(bytes, 8, 8));
        Assert.False(BulkExporter.HasPngDimensions(bytes.AsSpan(0, 22), 8, 4));
    }
}
