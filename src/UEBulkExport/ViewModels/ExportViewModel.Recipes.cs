using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed partial class ExportViewModel
{
    private const int BytesPerMebibyte = 1024 * 1024;

    [ObservableProperty] private string _recipeStatus = "";

    [RelayCommand]
    private async Task SaveRecipe()
    {
        if (!CanRun) return;
        RecipeStatus = "";
        try
        {
            var options = ToOptions();
            var path = await DialogService.SaveExportRecipeAsync();
            if (path is null || !CanRun) return;

            await Task.Run(() => ExportRecipe.Capture(options).Save(path));
            if (!_disposed) RecipeStatus = L["Export.Recipe.Saved"];
        }
        catch (UserFacingException)
        {
            if (!_disposed) RecipeStatus = L["Export.Recipe.SaveError"];
        }
    }

    [RelayCommand]
    private async Task LoadRecipe()
    {
        if (!CanRun) return;
        RecipeStatus = "";
        try
        {
            var path = await DialogService.PickExportRecipeAsync();
            if (path is null || !CanRun) return;
            var options = await Task.Run(() => ExportRecipe.Load(path).CreateOptions());
            if (!CanRun) return;
            if (!SupportsDesktopRecipe(options))
            {
                RecipeStatus = L["Export.Recipe.DesktopRange"];
                return;
            }

            var source = await DialogService.PickFolderAsync("Export.Recipe.PickSource", PaksPath);
            if (source is null || !CanRun) return;
            var output = await DialogService.PickFolderAsync("Export.Recipe.PickOutput", OutputPath);
            if (output is null || !CanRun) return;

            ApplyRecipe(options, source, output);
            RecipeStatus = L["Export.Recipe.Loaded"];
        }
        catch (UserFacingException)
        {
            if (!_disposed) RecipeStatus = L["Export.Recipe.LoadError"];
        }
    }

    private bool SupportsDesktopRecipe(Options options) =>
        Modes.Any(m => m.Mode == options.Mode) && Games.Any(g => g.Value == options.Game) &&
        options.Threads <= MaxThreads && options.MaxInFlightBytes % BytesPerMebibyte == 0 &&
        options.MaxInFlightBytes / BytesPerMebibyte <= 1048576 &&
        MeshFormats.Any(format => format.Value == options.MeshFormat) &&
        AnimFormats.Any(format => format.Value == options.AnimFormat) &&
        MeshQualities.Any(quality => quality.Value == options.MeshQuality) &&
        NaniteFormats.Any(format => format.Value == options.NaniteMeshFormat) &&
        TextureFormats.Any(format => format.Value == options.TextureFormat) &&
        SocketFormats.Any(format => format.Value == options.SocketFormat) &&
        Platforms.Any(platform => platform.Value == options.Platform);

    internal void ApplyRecipe(ExportRecipe recipe, string source, string output)
    {
        var options = recipe.CreateOptions();
        if (!SupportsDesktopRecipe(options)) throw new UserFacingException(L["Export.Recipe.DesktopRange"]);
        ApplyRecipe(options, source, output);
    }

    private void ApplyRecipe(Options options, string source, string output)
    {
        PaksPath = source;
        OutputPath = output;
        AesText = "";
        UsmapPath = "";
        SelectedMode = Modes.First(mode => mode.Mode == options.Mode);
        SetGame(Games.First(game => game.Value == options.Game));
        _gameChosenFor = PaksPath.Trim();
        EngineSource = EngineSource.Manual;

        Threads = options.Threads;
        MaxInFlightMiB = (int)(options.MaxInFlightBytes / BytesPerMebibyte);
        IncludeRegex = options.IncludeRegex ?? "";
        ExcludeRegex = options.ExcludeRegex == "" ? "(?:)" : options.ExcludeRegex ?? "";
        SetSelection(options.SelectedPaths);
        SkipJson = !options.WriteJson;
        SkipAssets = !options.WriteAssets;
        SkipRawMisc = !options.WriteRawMisc;
        RawPackages = options.WriteRawPackages;
        ExportMaterials = options.ExportMaterials;
        SkipAudioConvert = !options.ConvertAudio;
        SkipWorlds = !options.ExportWorlds;
        Overwrite = !options.Resume;
        Verbose = options.Verbose;
        MeshFormat = MeshFormats.First(format => format.Value == options.MeshFormat);
        AnimFormat = AnimFormats.First(format => format.Value == options.AnimFormat);
        MeshQuality = MeshQualities.First(quality => quality.Value == options.MeshQuality);
        NaniteFormat = NaniteFormats.First(format => format.Value == options.NaniteMeshFormat);
        TextureFormat = TextureFormats.First(format => format.Value == options.TextureFormat);
        SocketFormat = SocketFormats.First(format => format.Value == options.SocketFormat);
        Platform = Platforms.First(platform => platform.Value == options.Platform);
        SkipMorphs = !options.ExportMorphTargets;
        AllMips = options.ExportAllTextureMips;
        ScheduleScan(TimeSpan.FromMilliseconds(500));
    }
}
