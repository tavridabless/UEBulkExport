namespace UEBulkExport.Tests;

public sealed class DumpConversionTests
{
    [Theory]
    [InlineData("4.0", "ue4.0")]
    [InlineData("4.27", "ue4.27")]
    [InlineData("UE 4.18", "ue4.18")]
    public void ToUModelTag_maps_supported_UE4_versions(string version, string expected)
    {
        Assert.Equal(expected, DumpConversionService.ToUModelTag(version));
    }

    [Theory]
    [InlineData("3.5")]
    [InlineData("4.28")]
    [InlineData("5.0")]
    [InlineData("latest")]
    public void ToUModelTag_rejects_versions_unsupported_by_UE_Viewer(string version)
    {
        Assert.Throws<UserFacingException>(() => DumpConversionService.ToUModelTag(version));
    }

    [Theory]
    [InlineData("/Game", "/Game")]
    [InlineData("/Game/ConvertedDump/", "/Game/ConvertedDump")]
    [InlineData("\\Game\\Monsters", "/Game/Monsters")]
    public void NormalizeDestination_accepts_Unreal_content_paths(string value, string expected)
    {
        Assert.Equal(expected, DumpConversionService.NormalizeDestination(value));
    }

    [Theory]
    [InlineData("Content/Converted")]
    [InlineData("/Engine/Converted")]
    [InlineData("")]
    public void NormalizeDestination_rejects_non_Game_paths(string value)
    {
        Assert.Throws<UserFacingException>(() => DumpConversionService.NormalizeDestination(value));
    }

    [Fact]
    public void BuildAssetDestination_preserves_and_sanitizes_relative_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "Dump");
        var source = Path.Combine(root, "Skeletal Mesh", "Monster-01", "Body.gltf");

        var result = DumpConversionService.BuildAssetDestination(root, source, "/Game/ConvertedDump");

        Assert.Equal("/Game/ConvertedDump/Skeletal_Mesh/Monster_01", result);
    }

    [Fact]
    public void DetectTargetVersion_reads_engine_association()
    {
        using var temp = new TempDir();
        var project = Path.Combine(temp.Path, "Test.uproject");
        File.WriteAllText(project, "{\"EngineAssociation\":\"5.8\"}");

        Assert.Equal("5.8", DumpConversionService.DetectTargetVersion(project, ""));
    }

    [Fact]
    public void DetectTargetVersion_falls_back_to_editor_path()
    {
        var editor = Path.Combine("F:\\", "UNREAL", "UE_5.8", "Engine", "Binaries", "Win64",
            "UnrealEditor-Cmd.exe");

        Assert.Equal("5.8", DumpConversionService.DetectTargetVersion("", editor));
    }

    [Fact]
    public async Task Cancel_after_UE_Viewer_error_restores_temporarily_hidden_source_package()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        var source = temp.Dir("Dump");
        var package = Path.Combine(source, "BrokenTexture.uasset");
        var payload = Path.Combine(source, "BrokenTexture.ubulk");
        await File.WriteAllBytesAsync(package, [1]);
        await File.WriteAllBytesAsync(payload, []);
        var project = Path.Combine(temp.Path, "Target.uproject");
        await File.WriteAllTextAsync(project, "{\"EngineAssociation\":\"5.8\"}");
        var quickFailingExecutable = Path.Combine(Environment.SystemDirectory, "where.exe");
        var service = new DumpConversionService();
        var options = new DumpConversionOptions(
            source, "4.27", quickFailingExecutable, project, quickFailingExecutable,
            "/Game/Test", temp.Dir("Work"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(options, issueHandler: (_, _) => Task.FromResult(false)));

        Assert.True(File.Exists(package));
        Assert.False(File.Exists(package + ".uebskip"));
    }
}
