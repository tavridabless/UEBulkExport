using UEBulkExport.Gui.Services;

namespace UEBulkExport.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void Switches_turned_off_survive_a_save_and_reload()
    {
        var settings = new AppSettings
        {
            RememberPaths = false,
            ConfirmCloseWhileRunning = false,
            TransparencyEnabled = false
        };

        var reloaded = AppSettings.Deserialize(AppSettings.Serialize(settings));

        Assert.False(reloaded.RememberPaths);
        Assert.False(reloaded.ConfirmCloseWhileRunning);
        Assert.False(reloaded.TransparencyEnabled);
    }

    [Fact]
    public void Null_strings_in_the_file_become_empty_instead_of_crashing_the_pages()
    {
        const string json = """
            { "ConversionUsmapPath": null, "UModelPath": null, "LastPaksPath": null, "Recent": null }
            """;

        var loaded = AppSettings.Deserialize(json);

        Assert.Equal("", loaded.ConversionUsmapPath);
        Assert.Equal("", loaded.UModelPath);
        Assert.Equal("", loaded.LastPaksPath);
        Assert.NotNull(loaded.Recent);
    }

    [Fact]
    public void Null_strings_inside_recent_games_become_empty()
    {
        const string json = """
            { "Recent": [ { "Name": null, "PaksPath": "D:\\Games\\MyGame", "UsmapPath": null, "AesKeys": null } ] }
            """;

        var game = Assert.Single(AppSettings.Deserialize(json).Recent);

        Assert.Equal("", game.Name);
        Assert.Equal("", game.UsmapPath);
        Assert.NotNull(game.AesKeys);
    }
}
