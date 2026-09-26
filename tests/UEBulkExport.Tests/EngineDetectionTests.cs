using System.Text;

namespace UEBulkExport.Tests;

public sealed class EngineDetectionTests
{
    [Theory]
    [InlineData("++UE5+Release-5.3", "5.3")]
    [InlineData("++UE4+Release-4.27", "4.27")]
    public void ReadVersion_finds_the_ascii_branch_name(string branch, string expected)
    {
        using var temp = new TempDir();
        var exe = Path.Combine(temp.Path, "Game.exe");
        File.WriteAllBytes(exe, [.. new byte[1000], .. Encoding.ASCII.GetBytes(branch), 0, .. new byte[1000]]);

        Assert.Equal(expected, EngineDetection.ReadVersion(exe));
    }

    [Fact]
    public void ReadVersion_finds_a_utf16_branch_name()
    {
        using var temp = new TempDir();
        var exe = Path.Combine(temp.Path, "Game.exe");
        File.WriteAllBytes(exe, [.. new byte[999], .. Encoding.Unicode.GetBytes("++UE5+Release-5.1"), 0, 0]);

        Assert.Equal("5.1", EngineDetection.ReadVersion(exe));
    }

    [Theory]
    [InlineData("++Fortnite+Release-30.00")]
    [InlineData("++UE5+Release-4.27")]
    [InlineData("UE5 Release 5.3")]
    public void ReadVersion_ignores_other_branches(string text)
    {
        using var temp = new TempDir();
        var exe = Path.Combine(temp.Path, "Game.exe");
        File.WriteAllBytes(exe, Encoding.ASCII.GetBytes(text));

        Assert.Null(EngineDetection.ReadVersion(exe));
    }

    [Fact]
    public void ReadVersion_finds_a_name_that_straddles_two_chunks()
    {
        using var temp = new TempDir();
        var exe = Path.Combine(temp.Path, "Game.exe");
        var data = new byte[4 * 1024 * 1024 + 64];
        var branch = Encoding.ASCII.GetBytes("++UE5+Release-5.4");
        branch.CopyTo(data, 4 * 1024 * 1024 - 8);
        File.WriteAllBytes(exe, data);

        Assert.Equal("5.4", EngineDetection.ReadVersion(exe));
    }

    [Fact]
    public void DetectFromContainers_reads_the_largest_executable_of_the_game()
    {
        using var temp = new TempDir();
        var paks = temp.Dir("MyGame", "Content", "Paks");
        var binaries = temp.Dir("MyGame", "Binaries", "Win64");
        var game = new byte[9 * 1024 * 1024];
        Encoding.ASCII.GetBytes("++UE5+Release-5.2").CopyTo(game, 123);
        File.WriteAllBytes(Path.Combine(binaries, "MyGame-Win64-Shipping.exe"), game);
        File.WriteAllBytes(Path.Combine(binaries, "CrashHelper.exe"), Encoding.ASCII.GetBytes("++UE5+Release-5.0"));

        Assert.Equal("5.2", EngineDetection.DetectFromContainers(paks));
    }

    [Fact]
    public void DetectFromContainers_reads_the_engine_executable_of_a_blueprint_only_game()
    {
        using var temp = new TempDir();
        var paks = temp.Dir("Windows", "MyGame", "Content", "Paks");
        var binaries = temp.Dir("Windows", "Engine", "Binaries", "Win64");
        var game = new byte[9 * 1024 * 1024];
        Encoding.ASCII.GetBytes("++UE5+Release-5.6").CopyTo(game, 4096);
        File.WriteAllBytes(Path.Combine(binaries, "UnrealGame-Win64-Shipping.exe"), game);

        Assert.Equal("5.6", EngineDetection.DetectFromContainers(paks));
    }

    [Fact]
    public void DetectFromContainers_returns_null_outside_a_game_layout()
    {
        using var temp = new TempDir();

        Assert.Null(EngineDetection.DetectFromContainers(temp.Dir("Loose")));
    }
}
