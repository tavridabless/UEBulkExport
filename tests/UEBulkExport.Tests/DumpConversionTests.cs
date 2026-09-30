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
    [InlineData("5.0", 5, 0)]
    [InlineData("5.5", 5, 5)]
    [InlineData("UE 5.8", 5, 8)]
    public void ParseSourceVersion_accepts_supported_UE5_versions(string version, int major, int minor)
    {
        Assert.Equal((major, minor), DumpConversionService.ParseSourceVersion(version));
        Assert.True(DumpConversionService.IsUE5Source(version));
    }

    [Theory]
    [InlineData("4.28")]
    [InlineData("5.9")]
    [InlineData("6.0")]
    public void ParseSourceVersion_rejects_unsupported_versions(string version)
    {
        Assert.Throws<UserFacingException>(() => DumpConversionService.ParseSourceVersion(version));
    }

    [Fact]
    public void CreateUE5ExportOptions_uses_CUE4Parse_full_mode_without_editor_only_outputs()
    {
        var conversion = new DumpConversionOptions(
            "Dump", "5.5", "", "Target.uproject", "UnrealEditor-Cmd.exe", "/Game/Bodycam",
            MappingsPath: "Bodycam.usmap");

        var options = DumpConversionService.CreateUE5ExportOptions(conversion, "Dump", "Exported");

        Assert.Equal(CUE4Parse.UE4.Versions.EGame.GAME_UE5_5, options.Game);
        Assert.Equal(ExportMode.Full, options.Mode);
        Assert.Equal("Bodycam.usmap", options.UsmapPath);
        Assert.True(options.WriteAssets);
        Assert.False(options.WriteJson);
        Assert.False(options.WriteRawMisc);
        Assert.False(options.WriteRawPackages);
        Assert.False(options.ExportMaterials);
        Assert.False(options.ExportWorlds);
        Assert.True(options.Resume);

        var withoutMappings = DumpConversionService.CreateUE5ExportOptions(
            conversion with { MappingsPath = " " }, "Dump", "Exported");
        Assert.Null(withoutMappings.UsmapPath);
    }

    [Fact]
    public void CreateUE5ExportOptions_passes_an_existing_vgmstream_and_ignores_a_missing_one()
    {
        using var temp = new TempDir();
        var vgmstream = Path.Combine(temp.Path, "vgmstream-cli.exe");
        File.WriteAllBytes(vgmstream, [1]);
        var conversion = new DumpConversionOptions(
            "Dump", "5.5", "", "Target.uproject", "UnrealEditor-Cmd.exe", "/Game/Converted",
            VgmStreamPath: vgmstream);

        Assert.Equal(vgmstream, DumpConversionService.CreateUE5ExportOptions(conversion, "Dump", "Exported").VgmStreamPath);

        // A stale path from settings must not make every sound fail to start a process.
        var missing = conversion with { VgmStreamPath = Path.Combine(temp.Path, "gone.exe") };
        Assert.Null(DumpConversionService.CreateUE5ExportOptions(missing, "Dump", "Exported").VgmStreamPath);
    }

    [Fact]
    public void Encoded_audio_without_a_wav_is_reported_because_the_editor_cannot_import_it()
    {
        using var temp = new TempDir();
        var sounds = temp.Dir("Exported", "Game", "Audio");
        File.WriteAllBytes(Path.Combine(sounds, "Voice.binka"), [1]);
        File.WriteAllBytes(Path.Combine(sounds, "Music.binka"), [1]);
        File.WriteAllBytes(Path.Combine(sounds, "Music.wav"), [1]);     // converted: imports fine
        File.WriteAllBytes(Path.Combine(sounds, "Ambient.ogg"), [1]);   // importable as is
        File.WriteAllBytes(Path.Combine(sounds, "notes.txt"), [1]);     // not an asset at all

        var root = Path.Combine(temp.Path, "Exported");
        var failure = Assert.Single(DumpConversionService.FindUnconvertedAudio(root));

        Assert.Equal(Path.Combine("Game", "Audio", "Voice.binka"), failure.SourceFile);
        Assert.Contains("vgmstream", failure.Error);
    }

    [Fact]
    public void Validate_UE5_allows_missing_mappings_but_rejects_an_invalid_selected_path()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        var source = temp.Dir("Dump");
        var project = Path.Combine(temp.Path, "Target.uproject");
        var mappings = Path.Combine(temp.Path, "Bodycam.usmap");
        File.WriteAllText(project, "{\"EngineAssociation\":\"5.8\"}");
        File.WriteAllBytes(mappings, [1]);
        var executable = Path.Combine(Environment.SystemDirectory, "where.exe");

        var withoutMappings = new DumpConversionOptions(
            source, "5.5", "", project, executable, "/Game/Bodycam");
        DumpConversionService.Validate(withoutMappings);

        DumpConversionService.Validate(withoutMappings with { MappingsPath = mappings });

        var invalidMappings = withoutMappings with { MappingsPath = Path.Combine(temp.Path, "Missing.usmap") };
        Assert.Throws<UserFacingException>(() => DumpConversionService.Validate(invalidMappings));
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
    public async Task Cancel_after_UE_Viewer_error_leaves_the_source_dump_untouched()
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
        Assert.Equal(2, Directory.EnumerateFiles(source).Count());
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "Work", "Source")));
    }

    [Fact]
    public void BuildUnrealCommandLine_passes_the_script_with_forward_slashes_and_value_quoting()
    {
        var line = DumpConversionService.BuildUnrealCommandLine(
            @"C:\Unreal Projects\My Game\MyGame.uproject",
            @"C:\Unreal Projects\My Game\Saved\UEBulkExport\import-assets.py");

        Assert.StartsWith("\"C:\\Unreal Projects\\My Game\\MyGame.uproject\" ", line);
        Assert.EndsWith(
            "-ExecutePythonScript=\"C:/Unreal Projects/My Game/Saved/UEBulkExport/import-assets.py\"", line);
        Assert.DoesNotContain("\"-ExecutePythonScript", line);
    }

    [Fact]
    public void DefaultWorkingDirectory_separates_dumps_that_share_a_folder_name()
    {
        // Built from the platform's own separators: a Windows path is just a file name on Linux.
        var root = Path.GetTempPath();
        var project = Path.Combine(root, "P");
        var dumpA = Path.Combine(root, "Dumps", "A", "Content");
        var dumpB = Path.Combine(root, "Dumps", "B", "Content");

        var a = DumpConversionService.DefaultWorkingDirectory(project, dumpA, "4.27");
        var b = DumpConversionService.DefaultWorkingDirectory(project, dumpB, "4.27");
        var again = DumpConversionService.DefaultWorkingDirectory(project, dumpA + Path.DirectorySeparatorChar, "4.27");

        Assert.NotEqual(a, b);
        Assert.Equal(a, again);
        Assert.StartsWith(Path.Combine(project, "Saved", "UEBulkExport", "DumpConversion", "Content_4_27_"), a);

        // Windows paths are case-insensitive, so a differently cased path is the same dump there.
        if (OperatingSystem.IsWindows())
            Assert.Equal(a, DumpConversionService.DefaultWorkingDirectory(project, dumpA.ToLowerInvariant(), "4.27"),
                ignoreCase: true);
    }

    [Fact]
    public async Task FindFailedPackage_reads_only_the_current_attempt()
    {
        using var temp = new TempDir();
        var log = Path.Combine(temp.Path, "umodel.log");
        await File.WriteAllTextAsync(log, "******** Game/Old.uasset ********\nboom\n");
        var offset = new FileInfo(log).Length;
        await File.AppendAllTextAsync(log, "----- attempt -----\ncrashed before any banner\n");

        Assert.Equal("Game/Old.uasset", DumpConversionService.FindFailedPackage(log));
        Assert.Null(DumpConversionService.FindFailedPackage(log, offset));
    }

    [Fact]
    public void SourceMirror_leaves_the_dump_untouched_and_skips_excluded_packages()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        var source = temp.Dir("Dump");
        Directory.CreateDirectory(Path.Combine(source, "Maps"));
        File.WriteAllText(Path.Combine(source, "Good.uasset"), "good");
        File.WriteAllText(Path.Combine(source, "Bad.uasset"), "bad");
        var readOnly = Path.Combine(source, "Maps", "Level.umap");
        File.WriteAllText(readOnly, "map");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bad.uasset" };

        string root;
        using (var mirror = DumpConversionService.SourceMirror.Create(source, temp.Dir("Work"), excluded, default))
        {
            root = mirror.Root;
            Assert.True(File.Exists(Path.Combine(root, "Good.uasset")));
            Assert.True(File.Exists(Path.Combine(root, "Maps", "Level.umap")));
            Assert.False(File.Exists(Path.Combine(root, "Bad.uasset")));

            mirror.Exclude("Good.uasset");
            Assert.False(File.Exists(Path.Combine(root, "Good.uasset")));
        }

        Assert.False(Directory.Exists(root));
        Assert.Equal("good", File.ReadAllText(Path.Combine(source, "Good.uasset")));
        Assert.Equal("bad", File.ReadAllText(Path.Combine(source, "Bad.uasset")));
        Assert.True(File.GetAttributes(readOnly).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(3, Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count());
        File.SetAttributes(readOnly, FileAttributes.Normal);
    }
}
