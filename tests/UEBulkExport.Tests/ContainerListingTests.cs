using CUE4Parse.FileProvider.Objects;

namespace UEBulkExport.Tests;

public sealed class ContainerListingTests
{
    [Fact]
    public void One_pass_listing_matches_reference_groups_order_and_totals()
    {
        GameFile[] files =
        [
            new SyntheticGameFile("Game/Content/a.UASSET", 20),
            new SyntheticGameFile("game/Content/b.uasset", 30),
            new SyntheticGameFile("Engine/Config/c.INI", 25),
            new SyntheticGameFile("Game/Config/d.ini", 25),
            new SyntheticGameFile("loose", 50),
            new SyntheticGameFile("/leading/file.bin", 2),
            new SyntheticGameFile("Game/Ünicode/данные.bin", 3)
        ];

        var listing = BulkExporter.GetListing(files);
        var extensions = files.GroupBy(f => f.Extension.ToLowerInvariant())
            .Select(g => new ContainerListing.ExtensionGroup("." + g.Key, g.Count(), g.Sum(f => f.Size)))
            .OrderByDescending(g => g.Bytes).ToArray();
        var folders = files.GroupBy(f => f.Path.Split('/')[0])
            .Select(g => new ContainerListing.FolderGroup(g.Key, g.Count()))
            .OrderByDescending(g => g.Count).ToArray();

        Assert.Equal(extensions, listing.ByExtension);
        Assert.Equal(folders, listing.TopFolders);
        Assert.Equal(files.Length, listing.TotalCount);
        Assert.Equal(files.Sum(f => f.Size), listing.TotalBytes);
    }

    [Fact]
    public void Empty_listing_has_no_groups()
    {
        var listing = BulkExporter.GetListing([]);
        Assert.Empty(listing.ByExtension);
        Assert.Empty(listing.TopFolders);
        Assert.Equal(0, listing.TotalCount);
        Assert.Equal(0, listing.TotalBytes);
    }

    [Fact]
    public void Byte_totals_do_not_silently_overflow()
    {
        GameFile[] files = [new SyntheticGameFile("Root/a.bin", long.MaxValue), new SyntheticGameFile("Root/b.txt", 1)];
        Assert.Throws<OverflowException>(() => BulkExporter.GetListing(files));
    }
}
