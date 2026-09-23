using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;

namespace UEBulkExport.Tests;

public sealed class CliParseArgumentsTests
{
    private const string Key = "0x0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void Path_flags_map_to_their_properties()
    {
        var o = Cli.ParseArguments([
            "--paks", "P", "--out", "O", "--usmap", "U", "--oodle", "D", "--zlib", "Z",
            "--vgmstream", "V", "--retoc", "R"]);

        Assert.Equal("P", o.PaksDirectory);
        Assert.Equal("O", o.OutputDirectory);
        Assert.Equal("U", o.UsmapPath);
        Assert.Equal("D", o.OodlePath);
        Assert.Equal("Z", o.ZlibPath);
        Assert.Equal("V", o.VgmStreamPath);
        Assert.Equal("R", o.RetocPath);
    }

    [Fact]
    public void Enum_flags_are_parsed_case_insensitively()
    {
        var o = Cli.ParseArguments([
            "--game", "4.27", "--platform", "nintendoswitch", "--mode", "RAW", "--texture", "tga",
            "--quality", "lowest", "--nanite", "naniteonly", "--sockets", "none"]);

        Assert.Equal(EGame.GAME_UE4_27, o.Game);
        Assert.Equal(ETexturePlatform.NintendoSwitch, o.Platform);
        Assert.Equal(ExportMode.Raw, o.Mode);
        Assert.Equal(ETextureFormat.Tga, o.TextureFormat);
        Assert.Equal(EMeshQuality.Lowest, o.MeshQuality);
        Assert.Equal(ENaniteMeshFormat.NaniteOnly, o.NaniteMeshFormat);
        Assert.Equal(ESocketFormat.None, o.SocketFormat);
    }

    [Fact]
    public void Aes_is_repeatable_and_kept_in_order()
    {
        var o = Cli.ParseArguments(["--aes", "a", "--aes", "b"]);
        Assert.Equal(["a", "b"], o.AesKeys);
    }

    [Fact]
    public void Threads_include_exclude_mesh_and_anim_are_parsed()
    {
        var o = Cli.ParseArguments([
            "--threads", "7", "--include", "^Game/", "--exclude", "^Engine/",
            "--mesh", "psk", "--anim", "usda"]);

        Assert.Equal(7, o.Threads);
        Assert.Equal("^Game/", o.IncludeRegex);
        Assert.Equal("^Engine/", o.ExcludeRegex);
        Assert.Equal(EMeshFormat.ActorX, o.MeshFormat);
        Assert.Equal(EMeshFormat.USD, o.AnimFormatOverride);
    }

    [Fact]
    public void Boolean_switches_flip_their_properties()
    {
        var o = Cli.ParseArguments([
            "--no-json", "--no-assets", "--no-raw-misc", "--no-worlds", "--no-audio-convert",
            "--raw-packages", "--materials", "--overwrite", "--no-morphs", "--all-mips", "--dry-run",
            "--verbose"]);

        Assert.False(o.WriteJson);
        Assert.False(o.WriteAssets);
        Assert.False(o.WriteRawMisc);
        Assert.False(o.ExportWorlds);
        Assert.False(o.ConvertAudio);
        Assert.True(o.WriteRawPackages);
        Assert.True(o.ExportMaterials);
        Assert.False(o.Resume);
        Assert.False(o.ExportMorphTargets);
        Assert.True(o.ExportAllTextureMips);
        Assert.True(o.DryRun);
        Assert.True(o.Verbose);
    }

    [Fact]
    public void Missing_value_throws_user_facing_exception_naming_the_flag()
    {
        var e = Assert.Throws<UserFacingException>(() => Cli.ParseArguments(["--out", "x", "--paks"]));
        Assert.Contains("--paks", e.Headline);
        Assert.Contains("needs a value", e.Headline);
    }

    [Fact]
    public void Unknown_argument_throws()
    {
        var e = Assert.Throws<UserFacingException>(() => Cli.ParseArguments(["--bogus"]));
        Assert.Contains("--bogus", e.Headline);
        Assert.NotNull(e.Hint);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("many")]
    public void Threads_rejects_non_positive_values(string value)
    {
        Assert.Throws<UserFacingException>(() => Cli.ParseArguments(["--threads", value]));
    }

    [Fact]
    public void Unknown_enum_value_throws_with_valid_values_hint()
    {
        var e = Assert.Throws<UserFacingException>(() => Cli.ParseArguments(["--mode", "sideways"]));
        Assert.Contains("sideways", e.Headline);
        Assert.Contains("legacy", e.Hint);
    }

    [Fact]
    public void Aes_values_are_not_validated_while_parsing()
    {
        // Validation happens in Prepare; ParseArguments only collects.
        var o = Cli.ParseArguments(["--aes", Key, "--aes", "garbage"]);
        Assert.Equal(2, o.AesKeys.Count);
    }
}

public sealed class CliParseGameTests
{
    [Theory]
    [InlineData("5.3", EGame.GAME_UE5_3)]
    [InlineData("UE5_3", EGame.GAME_UE5_3)]
    [InlineData("GAME_UE5_3", EGame.GAME_UE5_3)]
    [InlineData("ue4_27", EGame.GAME_UE4_27)]
    [InlineData("4.27", EGame.GAME_UE4_27)]
    public void Accepts_dotted_underscored_and_full_names(string input, EGame expected)
    {
        Assert.Equal(expected, Cli.ParseGame(input));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("99.99")]
    public void Rejects_garbage(string input)
    {
        var e = Assert.Throws<UserFacingException>(() => Cli.ParseGame(input));
        Assert.Contains("Unknown engine version", e.Headline);
    }
}

public sealed class CliParseMeshAndAnimTests
{
    [Theory]
    [InlineData("gltf", EMeshFormat.Gltf2)]
    [InlineData("gltf2", EMeshFormat.Gltf2)]
    [InlineData("GLB", EMeshFormat.Gltf2)]
    [InlineData("actorx", EMeshFormat.ActorX)]
    [InlineData("psk", EMeshFormat.ActorX)]
    [InlineData("ueformat", EMeshFormat.UEFormat)]
    [InlineData("uemodel", EMeshFormat.UEFormat)]
    [InlineData("usd", EMeshFormat.USD)]
    [InlineData("usda", EMeshFormat.USD)]
    public void ParseMesh_accepts_aliases(string input, EMeshFormat expected)
    {
        Assert.Equal(expected, Cli.ParseMesh(input));
    }

    [Fact]
    public void ParseMesh_rejects_unknown_format()
    {
        Assert.Throws<UserFacingException>(() => Cli.ParseMesh("obj"));
    }

    [Theory]
    [InlineData("psk", EMeshFormat.ActorX)]
    [InlineData("uemodel", EMeshFormat.UEFormat)]
    [InlineData("usda", EMeshFormat.USD)]
    public void ParseAnim_accepts_non_gltf_formats(string input, EMeshFormat expected)
    {
        Assert.Equal(expected, Cli.ParseAnim(input));
    }

    [Theory]
    [InlineData("gltf")]
    [InlineData("gltf2")]
    [InlineData("glb")]
    public void ParseAnim_rejects_gltf(string input)
    {
        var e = Assert.Throws<UserFacingException>(() => Cli.ParseAnim(input));
        Assert.Contains("glTF", e.Headline);
    }
}

public sealed class CliAesKeyTests
{
    private const string Hex64 = "0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789ABCDEF";
    private const string Guid0 = "00000000-0000-0000-0000-000000000000";

    [Fact]
    public void SplitAesKey_bare_key_has_null_guid()
    {
        var (guid, key) = Cli.SplitAesKey($"0x{Hex64}");
        Assert.Null(guid);
        Assert.Equal($"0x{Hex64}", key);
    }

    [Fact]
    public void SplitAesKey_guid_prefix_is_split_off_and_trimmed()
    {
        var (guid, key) = Cli.SplitAesKey($"  {Guid0} : 0x{Hex64} ");
        Assert.Equal(Guid0, guid);
        Assert.Equal($"0x{Hex64}", key);
    }

    [Theory]
    [InlineData("0x" + Hex64)]
    [InlineData(Hex64)]
    [InlineData(Guid0 + ":0x" + Hex64)]
    [InlineData(Guid0 + ":" + Hex64)]
    public void ValidateAesKey_accepts_valid_forms(string raw)
    {
        Cli.ValidateAesKey(raw);
    }

    [Theory]
    [InlineData("0x1234")]
    [InlineData("0x" + Hex64 + "00")]
    [InlineData("0xZZ23456789abcdef0123456789ABCDEF0123456789abcdef0123456789ABCDEF")]
    [InlineData("")]
    public void ValidateAesKey_rejects_invalid_length_or_characters(string raw)
    {
        var e = Assert.Throws<UserFacingException>(() => Cli.ValidateAesKey(raw));
        Assert.Contains("not a valid AES key", e.Headline);
    }

    [Fact]
    public void ValidateAesKey_rejects_invalid_guid()
    {
        var e = Assert.Throws<UserFacingException>(() => Cli.ValidateAesKey($"not-a-guid:0x{Hex64}"));
        Assert.Contains("not a valid container GUID", e.Headline);
    }
}

public sealed class CliToArgumentsTests
{
    private const string Key = "0x0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private static Options NonDefault() => new()
    {
        PaksDirectory = @"C:\Games\Some Game\Content\Paks",
        OutputDirectory = @"D:\Export",
        Mode = ExportMode.Full,
        Game = EGame.GAME_UE4_27,
        UsmapPath = @"D:\maps\game.usmap",
        AesKeys = [Key, $"00000000-0000-0000-0000-000000000000:{Key}"],
        Threads = 3,
        IncludeRegex = "^Game/",
        ExcludeRegex = "^Engine/",
        WriteJson = false,
        WriteAssets = false,
        WriteRawMisc = false,
        ExportWorlds = false,
        ConvertAudio = false,
        WriteRawPackages = true,
        ExportMaterials = true,
        Resume = false,
        ExportMorphTargets = false,
        ExportAllTextureMips = true,
        MeshFormat = EMeshFormat.UEFormat,
        AnimFormatOverride = EMeshFormat.USD,
        TextureFormat = ETextureFormat.Tga,
        MeshQuality = EMeshQuality.All,
        NaniteMeshFormat = ENaniteMeshFormat.NaniteFirst,
        SocketFormat = ESocketFormat.Socket,
        Platform = Enum.GetValues<ETexturePlatform>().First(p => p != new Options().Platform),
        OodlePath = @"D:\bin\oo2core.dll",
        ZlibPath = @"D:\bin\zlib.dll",
        VgmStreamPath = @"D:\bin\vgmstream-cli.exe",
        RetocPath = @"D:\bin\retoc.exe",
        DryRun = true,
        Verbose = true
    };

    [Fact]
    public void Defaults_produce_only_paks_and_out()
    {
        var o = new Options { PaksDirectory = "P", OutputDirectory = "O" };
        Assert.Equal(["--paks", "P", "--out", "O"], Cli.ToArguments(o));
    }

    [Fact]
    public void Empty_options_produce_no_arguments()
    {
        Assert.Empty(Cli.ToArguments(new Options()));
    }

    [Fact]
    public void Round_trip_reproduces_every_non_default_option()
    {
        var original = NonDefault();
        var parsed = Cli.ParseArguments(Cli.ToArguments(original).ToArray());

        Assert.Equal(original.PaksDirectory, parsed.PaksDirectory);
        Assert.Equal(original.OutputDirectory, parsed.OutputDirectory);
        Assert.Equal(original.Mode, parsed.Mode);
        Assert.Equal(original.Game, parsed.Game);
        Assert.Equal(original.UsmapPath, parsed.UsmapPath);
        Assert.Equal(original.AesKeys, parsed.AesKeys);
        Assert.Equal(original.Threads, parsed.Threads);
        Assert.Equal(original.IncludeRegex, parsed.IncludeRegex);
        Assert.Equal(original.ExcludeRegex, parsed.ExcludeRegex);
        Assert.Equal(original.WriteJson, parsed.WriteJson);
        Assert.Equal(original.WriteAssets, parsed.WriteAssets);
        Assert.Equal(original.WriteRawMisc, parsed.WriteRawMisc);
        Assert.Equal(original.ExportWorlds, parsed.ExportWorlds);
        Assert.Equal(original.ConvertAudio, parsed.ConvertAudio);
        Assert.Equal(original.WriteRawPackages, parsed.WriteRawPackages);
        Assert.Equal(original.ExportMaterials, parsed.ExportMaterials);
        Assert.Equal(original.Resume, parsed.Resume);
        Assert.Equal(original.ExportMorphTargets, parsed.ExportMorphTargets);
        Assert.Equal(original.ExportAllTextureMips, parsed.ExportAllTextureMips);
        Assert.Equal(original.MeshFormat, parsed.MeshFormat);
        Assert.Equal(original.AnimFormatOverride, parsed.AnimFormatOverride);
        Assert.Equal(original.TextureFormat, parsed.TextureFormat);
        Assert.Equal(original.MeshQuality, parsed.MeshQuality);
        Assert.Equal(original.NaniteMeshFormat, parsed.NaniteMeshFormat);
        Assert.Equal(original.SocketFormat, parsed.SocketFormat);
        Assert.Equal(original.Platform, parsed.Platform);
        Assert.Equal(original.OodlePath, parsed.OodlePath);
        Assert.Equal(original.ZlibPath, parsed.ZlibPath);
        Assert.Equal(original.VgmStreamPath, parsed.VgmStreamPath);
        Assert.Equal(original.RetocPath, parsed.RetocPath);
        Assert.Equal(original.DryRun, parsed.DryRun);
        Assert.Equal(original.Verbose, parsed.Verbose);
    }

    [Fact]
    public void Round_trip_of_round_trip_is_stable()
    {
        var first = Cli.ToArguments(NonDefault());
        var second = Cli.ToArguments(Cli.ParseArguments(first.ToArray()));
        Assert.Equal(first, second);
    }

    [Fact]
    public void FormatCommand_quotes_paths_with_spaces_only()
    {
        var o = new Options { PaksDirectory = @"C:\Some Game\Paks", OutputDirectory = @"D:\Export" };

        var command = Cli.FormatCommand(o);

        Assert.Equal(@"UEBulkExport.Cli --paks ""C:\Some Game\Paks"" --out D:\Export", command);
    }

    [Fact]
    public void FormatCommand_escapes_embedded_double_quotes()
    {
        var o = new Options { PaksDirectory = "P", OutputDirectory = "O", IncludeRegex = "a\"b" };

        var command = Cli.FormatCommand(o);

        Assert.EndsWith("--include \"a\\\"b\"", command);
    }

    [Fact]
    public void FormatCommand_quotes_a_custom_executable_with_spaces()
    {
        var command = Cli.FormatCommand(new Options(), @"C:\Program Files\UEBulkExport.exe");
        Assert.Equal(@"""C:\Program Files\UEBulkExport.exe""", command);
    }
}

public sealed class CliPrepareTests : IDisposable
{
    private const string Key1 = "0x0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string Key2 = "0xFEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";

    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Missing_paks_throws()
    {
        var e = Assert.Throws<UserFacingException>(() => Cli.Prepare(new Options { OutputDirectory = "O" }));
        Assert.Contains("--paks is required", e.Headline);
    }

    [Fact]
    public void Missing_out_throws_outside_list_mode()
    {
        _tmp.File("x.pak");
        var o = new Options { PaksDirectory = _tmp.Path };

        var e = Assert.Throws<UserFacingException>(() => Cli.Prepare(o));
        Assert.Contains("--out is required", e.Headline);
    }

    [Fact]
    public void Missing_out_is_fine_in_list_mode()
    {
        _tmp.File("x.pak");
        var o = new Options { PaksDirectory = _tmp.Path, Mode = ExportMode.List };

        Cli.Prepare(o);

        Assert.Equal(Path.GetFullPath(_tmp.Path), o.PaksDirectory);
    }

    [Fact]
    public void Missing_usmap_file_throws()
    {
        _tmp.File("x.pak");
        var o = new Options
        {
            PaksDirectory = _tmp.Path,
            OutputDirectory = "O",
            UsmapPath = Path.Combine(_tmp.Path, "nope.usmap")
        };

        var e = Assert.Throws<UserFacingException>(() => Cli.Prepare(o));
        Assert.Contains("Mappings file not found", e.Headline);
    }

    [Fact]
    public void Folder_with_pak_resolves_to_its_full_path()
    {
        _tmp.File("x.pak");
        var o = new Options { PaksDirectory = _tmp.Path, OutputDirectory = "O" };

        Cli.Prepare(o);

        Assert.Equal(Path.GetFullPath(_tmp.Path), o.PaksDirectory);
    }

    [Fact]
    public void Invalid_aes_key_throws()
    {
        _tmp.File("x.pak");
        var o = new Options { PaksDirectory = _tmp.Path, OutputDirectory = "O", AesKeys = ["0xBAD"] };

        Assert.Throws<UserFacingException>(() => Cli.Prepare(o));
    }

    [Fact]
    public void Invalid_include_regex_throws()
    {
        _tmp.File("x.pak");
        var o = new Options { PaksDirectory = _tmp.Path, OutputDirectory = "O", IncludeRegex = "(" };

        var e = Assert.Throws<UserFacingException>(() => Cli.Prepare(o));
        Assert.Contains("--include", e.Headline);
    }

    [Fact]
    public void Legacy_with_utoc_and_include_throws_retoc_filter_error()
    {
        _tmp.File("game.utoc");
        var o = new Options
        {
            PaksDirectory = _tmp.Path,
            OutputDirectory = "O",
            Mode = ExportMode.Legacy,
            IncludeRegex = "^Game/"
        };

        var e = Assert.Throws<UserFacingException>(() => Cli.Prepare(o));
        Assert.Contains("--include/--exclude/--paths-file cannot be used", e.Headline);
    }

    [Fact]
    public void Legacy_with_utoc_and_two_aes_keys_throws()
    {
        _tmp.File("game.utoc");
        var o = new Options
        {
            PaksDirectory = _tmp.Path,
            OutputDirectory = "O",
            Mode = ExportMode.Legacy,
            AesKeys = [Key1, Key2]
        };

        var e = Assert.Throws<UserFacingException>(() => Cli.Prepare(o));
        Assert.Contains("only one AES key", e.Headline);
    }

    [Fact]
    public void Legacy_with_pak_only_allows_include_and_two_keys()
    {
        _tmp.File("game.pak");
        var o = new Options
        {
            PaksDirectory = _tmp.Path,
            OutputDirectory = "O",
            Mode = ExportMode.Legacy,
            IncludeRegex = "^Game/",
            AesKeys = [Key1, Key2]
        };

        Cli.Prepare(o);
    }

    [Fact]
    public void Raw_mode_with_utoc_allows_include()
    {
        _tmp.File("game.utoc");
        var o = new Options
        {
            PaksDirectory = _tmp.Path,
            OutputDirectory = "O",
            Mode = ExportMode.Raw,
            IncludeRegex = "^Game/"
        };

        Cli.Prepare(o);
    }
}
