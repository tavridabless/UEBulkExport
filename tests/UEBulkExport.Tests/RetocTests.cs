namespace UEBulkExport.Tests;

public sealed class RetocTests
{
    [Theory]
    [InlineData("GAME_UE5_7", true)]
    [InlineData("GAME_UE4_25", true)]
    [InlineData("GAME_UE5_8", false)]
    public void Only_engine_versions_known_to_retoc_are_passed_explicitly(string game, bool known)
    {
        Assert.Equal(known, Retoc.KnownEngineVersions.Contains(Retoc.ToRetocEngineVersion(game)!));
    }

    [Theory]
    [InlineData("GAME_UE5_3", "UE5_3")]
    [InlineData("GAME_UE4_27", "UE4_27")]
    [InlineData("game_ue5_0", "UE5_0")]
    public void ToRetocEngineVersion_maps_plain_engine_versions(string game, string expected)
    {
        Assert.Equal(expected, Retoc.ToRetocEngineVersion(game));
    }

    [Theory]
    [InlineData("GAME_Fortnite")]
    [InlineData("GAME_UE5")]
    [InlineData("GAME_UE5_3_Custom")]
    [InlineData("GAME_UEx_y")]
    [InlineData("")]
    public void ToRetocEngineVersion_returns_null_for_game_specific_or_malformed_names(string game)
    {
        Assert.Null(Retoc.ToRetocEngineVersion(game));
    }
}
