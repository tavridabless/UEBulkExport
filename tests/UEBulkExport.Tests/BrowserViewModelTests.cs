using System.Collections.Specialized;
using CUE4Parse.FileProvider.Objects;
using UEBulkExport.Gui.Services;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Tests;

public sealed class BrowserViewModelTests
{
    private static ScanResult Scan(params GameFile[] files) => new("synthetic", [], files);

    [Fact]
    public void Tree_merges_case_insensitive_folders_sorts_and_preserves_all_metadata()
    {
        using var vm = new BrowserViewModel();
        var scan = Scan(new SyntheticGameFile("Root/Z/b.bin", 2), new SyntheticGameFile("root/z/a.bin", 3),
            new SyntheticGameFile("Root/A/c.bin", 4));
        vm.Load(scan);
        var root = Assert.Single(vm.Roots);
        Assert.Equal(["A", "Z"], root.Children.Select(n => n.Name));
        Assert.Equal(["a.bin", "b.bin"], root.Children[1].Files.Select(f => f.Name));
        Assert.Equal(3, root.TotalCount);
        Assert.Equal(9, root.TotalBytes);
        Assert.Equal(scan.Files.Select(f => f.Path).Order(), root.AllFiles.Select(f => f.Path).Order());
    }

    [Fact]
    public void Pagination_reaches_every_file_and_preserves_selection_between_pages()
    {
        using var vm = new BrowserViewModel();
        var files = Enumerable.Range(0, 5002).Reverse()
            .Select(i => (GameFile)new SyntheticGameFile($"Root/Item{i:D5}.bin")).ToArray();
        vm.Load(Scan(files));
        Assert.Equal(5002, vm.MatchingRowCount);
        Assert.Equal(5000, vm.Rows.Count);
        Assert.Equal(2, vm.PageCount);
        Assert.False(vm.HasPreviousPage);
        var firstPath = vm.Rows[0].Path;
        vm.Rows[0].IsChecked = true;
        vm.NextPageCommand.Execute(null);
        Assert.Equal(2, vm.PageNumber);
        Assert.Equal(2, vm.Rows.Count);
        Assert.False(vm.HasNextPage);
        var lastPath = vm.Rows[^1].Path;
        vm.Rows[^1].IsChecked = true;
        IReadOnlySet<string>? selected = null;
        vm.ExportRequested += value => selected = value;
        vm.ExportSelectedCommand.Execute(null);
        Assert.NotNull(selected);
        Assert.True(selected.SetEquals([firstPath, lastPath]));
        vm.PreviousPageCommand.Execute(null);
        Assert.True(vm.Rows[0].IsChecked);
        vm.Search = "Item05001";
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(lastPath, Assert.Single(vm.Rows).Path);
        Assert.True(vm.Rows[0].IsChecked);
    }

    [Fact]
    public void Folder_rows_are_also_paged_and_type_filter_does_not_hide_navigation()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(Enumerable.Range(0, 5001)
            .Select(i => (GameFile)new SyntheticGameFile($"Root/Folder{i:D5}/entry.bin")).ToArray()));
        Assert.Equal(5000, vm.Rows.Count);
        Assert.Equal(5001, vm.MatchingRowCount);
        vm.Chip = vm.Chips.Single(c => c.Key == "maps");
        Assert.Equal(5001, vm.MatchingRowCount);
        vm.NextPageCommand.Execute(null);
        Assert.Single(vm.Rows);
        vm.Rows[0].IsChecked = true;
        Assert.Equal(1, vm.SelectedCount);
        vm.SelectedFolder = vm.Rows[0].Folder;
        Assert.Equal(1, vm.PageNumber);
        Assert.Empty(vm.Rows);
        vm.Chip = vm.Chips[0];
        Assert.True(Assert.Single(vm.Rows).IsChecked);
    }

    [Fact]
    public void Batch_refresh_emits_one_reset_and_no_per_row_notifications()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(Enumerable.Range(0, 6000)
            .Select(i => (GameFile)new SyntheticGameFile($"Root/Item{i:D5}.bin")).ToArray()));
        var actions = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);
        vm.Search = "Item0";
        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
        Assert.Equal(6000, vm.MatchingRowCount);
    }

    [Fact]
    public void Exclusion_and_clear_apply_to_underlying_files_not_only_the_current_page()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(new SyntheticGameFile("Root/A/one.bin"), new SyntheticGameFile("Root/A/two.bin"),
            new SyntheticGameFile("Root/B/three.bin")));
        vm.Rows[0].IsChecked = true;
        Assert.Equal(2, vm.SelectedCount);
        vm.ExcludeCheckedCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal(2, vm.ExcludedCount);
        Assert.True(vm.Rows[0].IsExcluded);
        IReadOnlySet<string>? selected = null;
        vm.ExportRequested += value => selected = value;
        vm.ExportSelectedCommand.Execute(null);
        Assert.Equal("Root/B/three.bin", Assert.Single(selected!));
        vm.Clear();
        Assert.False(vm.HasSelection);
        Assert.Equal(0, vm.ExcludedCount);
        Assert.Equal(0, vm.MatchingRowCount);
        Assert.False(vm.HasNextPage);
    }

    [Fact]
    public void Reload_clears_stale_selection_and_a_second_page()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(Enumerable.Range(0, 5001)
            .Select(i => (GameFile)new SyntheticGameFile($"Root/{i:D5}.bin")).ToArray()));
        vm.NextPageCommand.Execute(null);
        vm.Rows[0].IsChecked = true;
        vm.Load(Scan(new SyntheticGameFile("New/entry.bin")));
        Assert.False(vm.HasSelection);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal("New/entry.bin", Assert.Single(vm.Rows).Path);
    }
}
