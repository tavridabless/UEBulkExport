namespace UEBulkExport.Tests;

public sealed class DiscoveryResolvePaksDirectoryTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static string Normalise(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [Fact]
    public void Accepts_the_paks_folder_itself()
    {
        _tmp.File("pakchunk0-Windows.pak");

        var resolved = Discovery.ResolvePaksDirectory(_tmp.Path);

        Assert.Equal(Normalise(_tmp.Path), Normalise(resolved));
    }

    [Fact]
    public void Trims_whitespace_and_surrounding_quotes()
    {
        _tmp.File("x.pak");

        var resolved = Discovery.ResolvePaksDirectory($"  \"{_tmp.Path}\"  ");

        Assert.Equal(Normalise(_tmp.Path), Normalise(resolved));
    }

    [Fact]
    public void Accepts_a_parent_folder_and_finds_nested_content_paks()
    {
        var paks = Path.GetDirectoryName(_tmp.File("Content", "Paks", "x.pak"))!;
        _tmp.Dir("Binaries", "Win64");

        var resolved = Discovery.ResolvePaksDirectory(_tmp.Path);

        Assert.Equal(Normalise(paks), Normalise(resolved));
    }

    [Fact]
    public void Accepts_a_single_utoc_file_path()
    {
        var utoc = _tmp.File("global.utoc");

        var resolved = Discovery.ResolvePaksDirectory(utoc);

        Assert.Equal(Normalise(_tmp.Path), Normalise(resolved));
    }

    [Fact]
    public void Rejects_a_file_that_is_not_a_container()
    {
        var exe = _tmp.File("Game.exe");

        var e = Assert.Throws<UserFacingException>(() => Discovery.ResolvePaksDirectory(exe));
        Assert.Contains("not an Unreal container", e.Headline);
    }

    [Fact]
    public void Rejects_a_missing_path()
    {
        var missing = Path.Combine(_tmp.Path, "does-not-exist");

        var e = Assert.Throws<UserFacingException>(() => Discovery.ResolvePaksDirectory(missing));
        Assert.Contains("Path not found", e.Headline);
    }

    [Fact]
    public void Rejects_a_folder_tree_without_containers()
    {
        _tmp.File("Content", "readme.txt");

        var e = Assert.Throws<UserFacingException>(() => Discovery.ResolvePaksDirectory(_tmp.Path));
        Assert.Contains("No .utoc, .ucas or .pak files", e.Headline);
    }

    [Fact]
    public void Rejects_two_sibling_container_folders()
    {
        _tmp.File("GameA", "Content", "Paks", "a.pak");
        _tmp.File("GameB", "Content", "Paks", "b.utoc");

        var e = Assert.Throws<UserFacingException>(() => Discovery.ResolvePaksDirectory(_tmp.Path));
        Assert.Contains("Several container folders", e.Headline);
        Assert.Contains("GameA", e.Hint);
        Assert.Contains("GameB", e.Hint);
    }
}

public sealed class DiscoveryHasIoStoreContainersTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void True_when_a_utoc_is_present()
    {
        _tmp.File("x.pak");
        _tmp.File("global.UTOC");
        Assert.True(Discovery.HasIoStoreContainers(_tmp.Path));
    }

    [Fact]
    public void False_for_pak_only_folders()
    {
        _tmp.File("x.pak");
        _tmp.File("x.ucas");
        Assert.False(Discovery.HasIoStoreContainers(_tmp.Path));
    }
}

public sealed class DiscoveryFindMappingsTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    /// <summary>
    /// FindMappings also searches next to the test host and in the working directory. Those are
    /// outside the test's control, so count what is already there and adjust the expectation.
    /// </summary>
    private static int AmbientCount()
    {
        var dirs = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
        return dirs
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.usmap", SearchOption.TopDirectoryOnly))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    [Fact]
    public void Zero_candidates_returns_null()
    {
        var paks = _tmp.Dir("Content", "Paks");

        if (AmbientCount() > 0)
        {
            // Cannot observe the "nothing found" case; the ambient files already win or conflict.
            return;
        }

        Assert.Null(Discovery.FindMappings(paks, null));
    }

    [Fact]
    public void One_candidate_beside_the_containers_is_returned()
    {
        var paks = _tmp.Dir("Content", "Paks");
        var usmap = _tmp.File("Content", "Paks", "game.usmap");

        if (AmbientCount() > 0)
        {
            Assert.Throws<UserFacingException>(() => Discovery.FindMappings(paks, null));
            return;
        }

        Assert.Equal(Path.GetFullPath(usmap), Path.GetFullPath(Discovery.FindMappings(paks, null)!));
    }

    [Fact]
    public void One_candidate_one_level_above_the_containers_is_returned()
    {
        var paks = _tmp.Dir("Content", "Paks");
        var usmap = _tmp.File("Content", "game.usmap");

        if (AmbientCount() > 0)
        {
            Assert.Throws<UserFacingException>(() => Discovery.FindMappings(paks, null));
            return;
        }

        Assert.Equal(Path.GetFullPath(usmap), Path.GetFullPath(Discovery.FindMappings(paks, null)!));
    }

    [Fact]
    public void One_candidate_in_the_output_folder_is_returned()
    {
        var paks = _tmp.Dir("Content", "Paks");
        var output = _tmp.Dir("Out");
        var usmap = _tmp.File("Out", "game.usmap");

        if (AmbientCount() > 0)
        {
            Assert.Throws<UserFacingException>(() => Discovery.FindMappings(paks, output));
            return;
        }

        Assert.Equal(Path.GetFullPath(usmap), Path.GetFullPath(Discovery.FindMappings(paks, output)!));
    }

    [Fact]
    public void Two_candidates_throw_and_list_both()
    {
        var paks = _tmp.Dir("Content", "Paks");
        _tmp.File("Content", "Paks", "a.usmap");
        _tmp.File("Content", "b.usmap");

        var e = Assert.Throws<UserFacingException>(() => Discovery.FindMappings(paks, null));
        Assert.Contains("more than one .usmap", e.Headline);
        Assert.Contains("a.usmap", e.Hint);
        Assert.Contains("b.usmap", e.Hint);
    }
}
