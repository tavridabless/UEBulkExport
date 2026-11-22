using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;
using UEBulkExport.Gui.Services;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Tests;

public sealed class ExportRecipeTests
{
    [Fact]
    public void Json_roundtrip_includes_portable_options_and_selection_without_local_inputs()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "export.recipe.json");
        var key = "0x" + new string('A', 64);
        var localPaths = new[] { "local-source", "local-output", "local-map", "local-oodle", "local-zlib",
            "local-retoc", "local-vgmstream", "local-selection" };
        var options = new Options
        {
            PaksDirectory = localPaths[0], OutputDirectory = localPaths[1], UsmapPath = localPaths[2],
            OodlePath = localPaths[3], ZlibPath = localPaths[4], RetocPath = localPaths[5],
            VgmStreamPath = localPaths[6], PathsFile = localPaths[7], AesKeys = [key], DryRun = true,
            Mode = ExportMode.Full, Game = EGame.GAME_UE5_4, Threads = 3, MaxInFlightBytes = 123456789,
            IncludeRegex = "^Game/Content/", ExcludeRegex = @"\.umap$", WriteRawPackages = true,
            WriteRawMisc = false, WriteJson = false, ExportMaterials = true, ConvertAudio = false,
            ExportWorlds = false, Resume = false, ExportAllTextureMips = true,
            MeshFormat = EMeshFormat.USD, AnimFormatOverride = EMeshFormat.UEFormat,
            SelectedPaths = new HashSet<string> { "Game\\Content\\Model.uasset", "Game/Other.bin" }
        };

        ExportRecipe.Capture(options).Save(path);

        var text = File.ReadAllText(path);
        Assert.Contains("\"version\": 1", text);
        Assert.DoesNotContain(key, text, StringComparison.OrdinalIgnoreCase);
        foreach (var localPath in localPaths) Assert.DoesNotContain(localPath, text, StringComparison.OrdinalIgnoreCase);
        var saved = ExportRecipe.Load(path).CreateOptions();
        Assert.Equal(options.Mode, saved.Mode);
        Assert.Equal(options.Game, saved.Game);
        Assert.Equal(options.Threads, saved.Threads);
        Assert.Equal(options.MaxInFlightBytes, saved.MaxInFlightBytes);
        Assert.Equal(options.IncludeRegex, saved.IncludeRegex);
        Assert.Equal(options.ExcludeRegex, saved.ExcludeRegex);
        Assert.Equal(options.MeshFormat, saved.MeshFormat);
        Assert.Equal(options.AnimFormatOverride, saved.AnimFormatOverride);
        Assert.Equal(options.ExportAllTextureMips, saved.ExportAllTextureMips);
        Assert.Equal(options.WriteRawPackages, saved.WriteRawPackages);
        Assert.Equal(options.WriteRawMisc, saved.WriteRawMisc);
        Assert.Equal(options.ConvertAudio, saved.ConvertAudio);
        Assert.Equal(options.Resume, saved.Resume);
        Assert.Equal(["Game/Content/Model.uasset", "Game/Other.bin"], saved.SelectedPaths);
        Assert.Empty(saved.PaksDirectory);
        Assert.Empty(saved.OutputDirectory);
        Assert.Empty(saved.AesKeys);
        Assert.False(saved.DryRun);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":1,\"version\":1}")]
    [InlineData("{\"version\":1,\"aesKeys\":[]}")]
    [InlineData("{\"version\":1,\"paksDirectory\":\"local\"}")]
    [InlineData("{\"version\":1,\"mode\":3}")]
    [InlineData("{\"version\":1,\"threads\":0}")]
    [InlineData("{\"version\":1,\"maxInFlightBytes\":-1}")]
    [InlineData("{\"version\":1,\"includeRegex\":\"[\"}")]
    [InlineData("{\"version\":1,\"selectedPaths\":[null]}")]
    [InlineData("{\"version\":1,\"selectedPaths\":[\"C:/Local/notes.bin\"]}")]
    public void Recipe_rejects_invalid_or_nonportable_data(string json)
    {
        using var temp = new TempDir();
        var path = temp.File("export.recipe.json");
        File.WriteAllText(path, json);

        Assert.Throws<UserFacingException>(() => ExportRecipe.Load(path));
    }

    [Fact]
    public void Active_Aes_key_in_a_filter_is_refused_without_echoing_the_key()
    {
        var key = "0x" + new string('b', 64);
        var error = Assert.Throws<UserFacingException>(() =>
            ExportRecipe.Capture(new Options { AesKeys = [key], IncludeRegex = $"^{key}$" }));
        Assert.DoesNotContain(key, error.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_embeds_selection_and_run_requires_its_own_local_inputs()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "export.recipe.json");
        var localSelection = temp.File("local-selection.txt");
        File.WriteAllLines(localSelection, ["# selection", "Game/Content/Model.uasset", ""]);
        Assert.Null(RecipeCommand.Execute(["recipe", "save", path, "--mode", "raw", "--paths-file", localSelection]));
        File.Delete(localSelection);
        Assert.Throws<UserFacingException>(() => RecipeCommand.Execute(["recipe", "run", path]));

        var source = temp.File("Game", "fixture.pak");
        var output = Path.Combine(temp.Path, "Export");
        var options = RecipeCommand.Execute(["recipe", "run", path,
            "--paks", source, "--out", output, "--dry-run"]);

        Assert.NotNull(options);
        Assert.Equal(Path.GetDirectoryName(source), options.PaksDirectory);
        Assert.Equal(output, options.OutputDirectory);
        Assert.Equal(["Game/Content/Model.uasset"], options.SelectedPaths);
        Assert.Null(options.PathsFile);
        Assert.True(options.DryRun);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Run_rejects_portable_settings_as_local_overrides()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "export.recipe.json");
        ExportRecipe.Capture(new Options()).Save(path);
        Assert.Throws<UserFacingException>(() => RecipeCommand.Execute(["recipe", "run", path, "--include", ".*"]));
    }

    [Fact]
    public void Desktop_import_applies_options_to_the_selected_folders_and_clears_the_old_key()
    {
        using var vm = new ExportViewModel(new AppSettings { RememberPaths = false }, new ExportService(),
            action => action(), autoScan: false, persistPreferences: false, trackPerformance: false);
        vm.AesText = "old-aes";
        vm.UsmapPath = "old-map";
        vm.RetocPath = "local-retoc";
        var recipe = ExportRecipe.Capture(new Options
        {
            Mode = ExportMode.Raw, Game = EGame.GAME_UE5_4, Threads = 1, MaxInFlightBytes = 64L * 1024 * 1024,
            IncludeRegex = "^Game/ ", ExcludeRegex = "Skip", SelectedPaths = new HashSet<string> { "Game/file.bin" },
            WriteRawMisc = false, MeshFormat = EMeshFormat.USD, AnimFormatOverride = EMeshFormat.ActorX
        });

        vm.ApplyRecipe(recipe, "chosen-source", "chosen-output");

        var options = vm.ToOptions();
        Assert.Equal("chosen-source", options.PaksDirectory);
        Assert.Equal("chosen-output", options.OutputDirectory);
        Assert.Equal("local-retoc", options.RetocPath);
        Assert.Empty(options.AesKeys);
        Assert.Null(options.UsmapPath);
        Assert.Equal(recipe.Mode, options.Mode);
        Assert.Equal(recipe.Game, options.Game);
        Assert.Equal(recipe.IncludeRegex, options.IncludeRegex);
        Assert.Equal(recipe.MaxInFlightBytes, options.MaxInFlightBytes);
        Assert.Equal(recipe.SelectedPaths, options.SelectedPaths);
        Assert.Equal(recipe.AnimFormatOverride, options.AnimFormatOverride);
    }
}
