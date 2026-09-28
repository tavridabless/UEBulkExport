namespace UEBulkExport.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public void WriteAllBytes_replaces_the_destination_without_leaving_a_partial_file()
    {
        using var temp = new TempDir();
        var path = temp.File("Output", "asset.uasset");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!,
            "*" + AtomicFile.TemporarySuffix));
    }

    [Fact]
    public void Publish_atomically_replaces_an_existing_file()
    {
        using var temp = new TempDir();
        var destination = temp.File("Output", "asset.uexp");
        File.WriteAllText(destination, "old");
        var staged = temp.File("Stage", "asset.uexp");
        File.WriteAllText(staged, "complete");

        AtomicFile.Publish(staged, destination);

        Assert.Equal("complete", File.ReadAllText(destination));
        Assert.False(File.Exists(staged));
    }
}
