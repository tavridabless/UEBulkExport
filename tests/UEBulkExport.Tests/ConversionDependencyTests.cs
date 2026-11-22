using System.Reflection;
using CUE4Parse_Conversion.Dto;

namespace UEBulkExport.Tests;

public sealed class ConversionDependencyTests
{
    [Fact]
    public void Landscape_converter_resolves_patched_imagesharp()
    {
        var heightmap = typeof(LandscapeMeshDto).GetField("HeightmapTexture");
        Assert.NotNull(heightmap);
        var imageAssembly = heightmap.FieldType.Assembly;
        Assert.Equal("SixLabors.ImageSharp", imageAssembly.GetName().Name);
        var version = imageAssembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        Assert.True(Version.TryParse(version, out var installedVersion));
        Assert.True(installedVersion >= new Version(3, 2, 0));
    }
}
