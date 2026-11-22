using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Versions;

namespace UEBulkExport.Tests;

public sealed class CompareBuildsTests
{
    [Fact]
    public void Metadata_compares_sizes_and_reports_changes_in_path_order_without_reading_entries()
    {
        var before = Files(new("Content/size.bin", 3), new("Content/gone.bin"), new("Content/same.bin"));
        var after = Files(new("Content/size.bin", 4), new("Content/added.bin"), new("content/Same.bin"));

        var result = Run(before, after, hash: false);

        Assert.False(result.HashContents);
        Assert.Equal((1, 1, 1, 1), (result.Added, result.Removed, result.Changed, result.Unchanged));
        Assert.Collection(result.Entries,
            entry => Assert.Equal(new(CompareChange.Added, "Content/added.bin", null, 1), entry),
            entry => Assert.Equal(new(CompareChange.Removed, "Content/gone.bin", 1, null), entry),
            entry => Assert.Equal(new(CompareChange.Changed, "Content/size.bin", 3, 4), entry));
    }

    [Fact]
    public void Hashing_checks_same_size_edits_and_uses_the_after_build_path()
    {
        var before = Files(new("content/file.bin", 2, () => [1, 2]), new("unchanged.bin", 2, () => [3, 4]));
        var after = Files(new("Content/File.bin", 2, () => [1, 3]), new("unchanged.bin", 2, () => [3, 4]));

        var result = Run(before, after, hash: true);

        Assert.True(result.HashContents);
        Assert.Equal((0, 0, 1, 1), (result.Added, result.Removed, result.Changed, result.Unchanged));
        Assert.Equal(new(CompareChange.Changed, "Content/File.bin", 2, 2), Assert.Single(result.Entries));
    }

    [Fact]
    public void Hash_read_errors_stop_the_comparison_and_identify_the_affected_build()
    {
        var before = Files(new SyntheticGameFile("asset.uasset", 1, () => [9]));
        var after = Files(new SyntheticGameFile("asset.uasset", 1, () => throw new IOException("read failed")));

        var failure = Assert.Throws<UserFacingException>(() => Run(before, after, hash: true));

        Assert.StartsWith("After build:", failure.Headline);
        Assert.Contains("asset.uasset", failure.Headline);
    }

    [Fact]
    public void Cancellation_between_hash_reads_does_not_read_the_after_entry()
    {
        using var cancellation = new CancellationTokenSource();
        var before = Files(new SyntheticGameFile("asset.bin", 1, () =>
        {
            cancellation.Cancel();
            return [1];
        }));
        var after = Files(new SyntheticGameFile("asset.bin", 1,
            () => throw new InvalidOperationException("Should not be read.")));

        Assert.ThrowsAny<OperationCanceledException>(() => Run(before, after, hash: true, cancellation.Token));
    }

    [Fact]
    public void Pak_group_exports_unchanged_companions_and_preserves_why_it_changed()
    {
        var before = Files(new("Game/Content/Panel.uasset", 10), new("Game/Content/Panel.uexp", 2),
            new("Game/Content/Panel.ubulk", 30), new("Game/Content/Panel.uptnl", 4));
        var after = Files(new("Game/Content/Panel.uasset", 10), new("Game/Content/Panel.uexp", 3),
            new("Game/Content/Panel.ubulk", 30));

        var group = Assert.Single(Run(before, after, hash: false).Groups);

        Assert.Equal(CompareGroupKind.PakPackage, group.Kind);
        Assert.Equal(CompareChange.Changed, group.Change);
        Assert.Equal("Game/Content/Panel.uasset", group.Path);
        Assert.Equal(["Game/Content/Panel.uasset", "Game/Content/Panel.ubulk", "Game/Content/Panel.uexp"],
            group.AfterPaths);
        Assert.Equal([CompareChange.Changed, CompareChange.Removed], group.Changes.Select(entry => entry.Change));
        Assert.Equal(["Game/Content/Panel.uexp", "Game/Content/Panel.uptnl"], group.Changes.Select(entry => entry.Path));
    }

    [Fact]
    public void Removed_packages_and_ambiguous_or_orphan_parts_have_safe_after_files()
    {
        var before = Files(new("Old/Level.umap"), new("Old/Level.uexp"), new("New/Mixed.uasset"),
            new("New/Mixed.umap"), new("New/Mixed.uexp"));
        var after = Files(new("New/Mixed.uasset"), new("New/Mixed.umap"), new("New/Mixed.uexp", 2),
            new("New/Orphan.ubulk"));

        var groups = Run(before, after, hash: false).Groups;

        Assert.Collection(groups,
            group =>
            {
                Assert.Equal(CompareGroupKind.File, group.Kind);
                Assert.Equal(["New/Mixed.uexp"], group.AfterPaths);
            },
            group =>
            {
                Assert.Equal(CompareGroupKind.File, group.Kind);
                Assert.Equal(["New/Orphan.ubulk"], group.AfterPaths);
            },
            group =>
            {
                Assert.Equal(CompareGroupKind.PakPackage, group.Kind);
                Assert.Equal(CompareChange.Removed, group.Change);
                Assert.Equal(2, group.Changes.Count);
                Assert.Empty(group.AfterPaths);
            });
    }

    [Fact]
    public void Manifest_round_trips_selected_removed_group_and_contains_no_local_path()
    {
        using var temp = new TempDir();
        var beforeDir = temp.Dir("old-location");
        var afterDir = temp.Dir("new-location");
        var beforeFile = temp.File("old-location", "build.pak");
        var afterFile = temp.File("new-location", "build.pak");
        File.WriteAllText(beforeFile, "before");
        File.WriteAllText(afterFile, "after");
        var before = Files(new SyntheticGameFile("Game/Removed.bin"));
        var after = Files(new SyntheticGameFile("Game/Added.bin"));
        var result = Run(before, after, hash: false) with
        {
            BeforeSnapshot = CompareSnapshot.Capture(beforeDir, EGame.GAME_UE5_3, false, [beforeFile]),
            AfterSnapshot = CompareSnapshot.Capture(afterDir, EGame.GAME_UE5_3, false, [afterFile])
        };

        var path = Path.Combine(temp.Path, "delta.json");
        CompareManifest.Capture(result, new HashSet<string> { "file:Game/Removed.bin" }).Save(path);
        var text = File.ReadAllText(path);
        var loaded = CompareManifest.Load(path);
        Assert.Equal(CompareManifest.CurrentVersion, loaded.Version);
        Assert.Equal("file:Game/Removed.bin", Assert.Single(loaded.Groups).Id);
        Assert.Empty(loaded.Groups[0].AfterPaths);
        Assert.DoesNotContain("old-location", text);
        Assert.DoesNotContain("new-location", text);

        File.WriteAllText(path, text.Replace("Game/Removed.bin", "C:/Removed.bin", StringComparison.Ordinal));
        Assert.Throws<UserFacingException>(() => CompareManifest.Load(path));
    }

    [Fact]
    public void Archive_identity_travels_with_exact_bytes_and_all_companion_parts()
    {
        using var temp = new TempDir();
        var left = temp.Dir("left");
        var right = temp.Dir("right");
        var leftParts = new[] { temp.File("left", "data.utoc"), temp.File("left", "data_s1.ucas") };
        var rightParts = new[] { temp.File("right", "data.utoc"), temp.File("right", "data_s1.ucas") };
        File.WriteAllText(leftParts[0], "toc");
        File.WriteAllText(leftParts[1], "abc");
        File.WriteAllText(rightParts[0], "toc");
        File.WriteAllText(rightParts[1], "abc");
        var identity = CompareSnapshot.Capture(left, EGame.GAME_UE5_3, false, leftParts).Identity(default);
        Assert.Equal(identity,
            CompareSnapshot.Capture(right, EGame.GAME_UE5_3, false, rightParts.Reverse()).Identity(default));

        File.WriteAllText(rightParts[1], "zbc");
        Assert.NotEqual(identity,
            CompareSnapshot.Capture(right, EGame.GAME_UE5_3, false, rightParts).Identity(default));
        Assert.NotEqual(identity,
            CompareSnapshot.Capture(right, EGame.GAME_UE5_3, false, [rightParts[0]]).Identity(default));
    }

    [Fact]
    public void Snapshot_rejects_changed_archives_and_honours_cancellation()
    {
        using var temp = new TempDir();
        var file = temp.File("build.pak");
        var snapshot = CompareSnapshot.Capture(temp.Path, EGame.GAME_UE5_3, false, [file]);
        File.WriteAllText(file, "changed");

        Assert.Throws<UserFacingException>(() => snapshot.Identity(default));
        Assert.Throws<OperationCanceledException>(() => snapshot.Identity(new CancellationToken(canceled: true)));
    }

    private static Dictionary<string, GameFile> Files(params SyntheticGameFile[] entries) =>
        entries.ToDictionary(file => file.Path, file => (GameFile)file, StringComparer.OrdinalIgnoreCase);

    private static CompareResult Run(IReadOnlyDictionary<string, GameFile> before,
        IReadOnlyDictionary<string, GameFile> after, bool hash, CancellationToken ct = default) =>
        CompareBuilds.CompareEntries(before, after, hash, "After", EGame.GAME_UE5_3, true, ct: ct);
}
