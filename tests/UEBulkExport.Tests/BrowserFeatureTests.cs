using System.Collections.Specialized;
using System.Reflection;
using CUE4Parse.FileProvider.Objects;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;
using UEBulkExport.Gui.ViewModels;
using UEBulkExport.Gui.Views;

namespace UEBulkExport.Tests;

public sealed class BrowserFeatureTests
{
    private static ScanResult Scan(params GameFile[] files) => new("synthetic", [], files);

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(764, false, false)]
    [InlineData(899.99, false, false)]
    [InlineData(900, false, true)]
    [InlineData(1200, false, true)]
    [InlineData(764, true, true)]
    [InlineData(1200, true, true)]
    public void Empty_details_are_collapsed_on_narrow_views_but_a_focused_entry_restores_them(
        double width, bool hasDetail, bool expected) =>
        Assert.Equal(expected, BrowserView.ShouldShowDetails(width, hasDetail));

    [Fact]
    public void Recursive_search_is_opt_in_stays_inside_the_selected_tree_and_matches_paths()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(new SyntheticGameFile("Root/A/Sub/Needle.ini", 3),
            new SyntheticGameFile("Root/A/direct.bin", 2),
            new SyntheticGameFile("Root/Amazing/Needle.ini", 8),
            new SyntheticGameFile("Other/Needle.ini", 16)));
        Assert.False(vm.IncludeSubfolders);
        Assert.Equal(BrowserSortField.Name, vm.SortField);
        Assert.False(vm.SortDescending);
        vm.SelectedFolder = vm.Roots.Single(n => n.Name == "Root").Children.Single(n => n.Name == "A");
        vm.Search = "needle";
        Assert.Empty(vm.Rows);
        vm.IncludeSubfolders = true;
        var row = Assert.Single(vm.Rows);
        Assert.False(row.IsFolder);
        Assert.Equal("Root/A/Sub/Needle.ini", row.Path);
        Assert.Equal(1, vm.MatchingFileCount);
        Assert.Equal(3, vm.MatchingBytes);
        vm.Search = "a/sub/";
        Assert.Equal(row.Path, Assert.Single(vm.Rows).Path);
        vm.Chip = vm.Chips.Single(c => c.Key == "media");
        Assert.Empty(vm.Rows);
        Assert.False(vm.SelectAllMatchesCommand.CanExecute(null));
        vm.Chip = vm.Chips.Single(c => c.Key == "config");
        Assert.Equal(row.Path, Assert.Single(vm.Rows).Path);
    }

    [Fact]
    public void Name_size_and_type_sort_have_deterministic_ties_and_keep_navigation_first()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(new SyntheticGameFile("Root/Z/nested.bin", 2),
            new SyntheticGameFile("Root/A/nested.bin", 8),
            new SyntheticGameFile("Root/z.ini", 3), new SyntheticGameFile("Root/B.bin", 1),
            new SyntheticGameFile("Root/a.ini", 1)));
        Assert.Equal(["A", "Z", "a.ini", "B.bin", "z.ini"], vm.Rows.Select(r => r.Name));
        vm.SortBySizeCommand.Execute(null);
        Assert.False(vm.SortDescending);
        Assert.Equal(["Z", "A", "a.ini", "B.bin", "z.ini"], vm.Rows.Select(r => r.Name));
        vm.SortBySizeCommand.Execute(null);
        Assert.True(vm.SortDescending);
        Assert.Equal(["A", "Z", "z.ini", "a.ini", "B.bin"], vm.Rows.Select(r => r.Name));
        vm.SortByTypeCommand.Execute(null);
        Assert.False(vm.SortDescending);
        Assert.Equal(["A", "Z", "B.bin", "a.ini", "z.ini"], vm.Rows.Select(r => r.Name));
        vm.SortByNameCommand.Execute(null);
        Assert.False(vm.SortDescending);
        vm.SortByNameCommand.Execute(null);
        Assert.Equal(["Z", "A", "z.ini", "B.bin", "a.ini"], vm.Rows.Select(r => r.Name));
    }

    [Fact]
    public void Equal_file_names_in_a_subtree_use_full_path_as_the_stable_tie_break()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(new SyntheticGameFile("Root/Z/same.bin", 10),
            new SyntheticGameFile("Root/A/same.bin", 10), new SyntheticGameFile("Root/B/same.bin", 10)));
        vm.IncludeSubfolders = true;
        var expected = new[] { "Root/A/same.bin", "Root/B/same.bin", "Root/Z/same.bin" };
        Assert.Equal(expected, vm.Rows.Select(r => r.Path));
        vm.SortBySizeCommand.Execute(null);
        Assert.Equal(expected, vm.Rows.Select(r => r.Path));
        vm.SortBySizeCommand.Execute(null);
        Assert.Equal(expected, vm.Rows.Select(r => r.Path));
        vm.SortByTypeCommand.Execute(null);
        Assert.Equal(expected, vm.Rows.Select(r => r.Path));
    }

    [Fact]
    public void Select_all_matches_crosses_pages_and_does_not_select_non_matching_folder_contents()
    {
        using var vm = new BrowserViewModel();
        var files = Enumerable.Range(0, 5002)
            .Select(i => (GameFile)new SyntheticGameFile($"Root/Sub/Needle{i:D5}.ini", 2))
            .Concat([new SyntheticGameFile("Root/Sub/other.ini", 7),
                new SyntheticGameFile("Root/Sub/Needle.bin", 8)]).ToArray();
        vm.Load(Scan(files));
        vm.IncludeSubfolders = true;
        vm.Search = "Needle";
        vm.Chip = vm.Chips.Single(c => c.Key == "config");
        Assert.Equal(5002, vm.MatchingFileCount);
        Assert.Equal(10004, vm.MatchingBytes);
        vm.SelectAllMatchesCommand.Execute(null);
        Assert.Equal(5002, vm.SelectedCount);
        Assert.All(vm.Rows, r => Assert.True(r.IsChecked));
        vm.NextPageCommand.Execute(null);
        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.True(r.IsChecked));
        IReadOnlySet<string>? selected = null;
        vm.ExportRequested += paths => selected = paths;
        vm.ExportSelectedCommand.Execute(null);
        Assert.NotNull(selected);
        Assert.Equal(5002, selected.Count);
        Assert.DoesNotContain("Root/Sub/other.ini", selected);
        Assert.DoesNotContain("Root/Sub/Needle.bin", selected);
    }

    [Fact]
    public void Checked_excluded_and_focused_entries_survive_search_sort_and_scope_changes()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(new SyntheticGameFile("Root/selected.bin", 3), new SyntheticGameFile("Root/excluded.bin", 2),
            new SyntheticGameFile("Root/Sub/nested.bin", 1)));
        var excluded = vm.Rows.Single(r => r.Name == "excluded.bin");
        excluded.IsChecked = true;
        vm.ExcludeCheckedCommand.Execute(null);
        var selected = vm.Rows.Single(r => r.Name == "selected.bin");
        selected.IsChecked = true;
        vm.FocusedRow = selected;
        vm.SortBySizeCommand.Execute(null);
        Assert.Equal(selected.Path, vm.FocusedRow!.Path);
        Assert.True(vm.FocusedRow.IsChecked);
        Assert.True(vm.Rows.Single(r => r.Name == "excluded.bin").IsExcluded);
        vm.Search = "selected";
        Assert.True(Assert.Single(vm.Rows).IsChecked);
        vm.IncludeSubfolders = true;
        Assert.True(Assert.Single(vm.Rows).IsChecked);
        vm.Search = "excluded";
        Assert.True(Assert.Single(vm.Rows).IsExcluded);
        Assert.Null(vm.FocusedRow);
        vm.ClearSelectionCommand.Execute(null);
        Assert.False(vm.HasSelection);
        Assert.False(vm.Rows[0].IsExcluded);
    }

    [Fact]
    public void Searching_filtering_and_paging_reuse_one_sorted_scope_and_emit_one_reset_each()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(Enumerable.Range(0, 6000)
            .Select(i => (GameFile)new SyntheticGameFile($"Root/Sub/Item{i:D5}.ini")).ToArray()));
        vm.IncludeSubfolders = true;
        var field = typeof(BrowserViewModel).GetField("_scopeFiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cached = field.GetValue(vm);
        var actions = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);
        vm.Search = "Item";
        vm.Chip = vm.Chips.Single(c => c.Key == "config");
        vm.NextPageCommand.Execute(null);
        Assert.Same(cached, field.GetValue(vm));
        Assert.Equal(Enumerable.Repeat(NotifyCollectionChangedAction.Reset, 3), actions);
        actions.Clear();
        vm.SortBySizeCommand.Execute(null);
        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(6000, vm.MatchingFileCount);
    }

    [Fact]
    public void Selecting_matches_does_not_expand_matching_navigation_folders()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(new SyntheticGameFile("Root/NeedleFolder/unrelated.bin"),
            new SyntheticGameFile("Root/Needle.ini")));
        vm.Search = "Needle";
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(1, vm.MatchingFileCount);
        vm.SelectAllMatchesCommand.Execute(null);
        Assert.Equal(1, vm.SelectedCount);
        Assert.False(vm.Rows.Single(r => r.IsFolder).IsChecked);
        Assert.True(vm.Rows.Single(r => !r.IsFolder).IsChecked);
    }

    [Fact]
    public async Task Copy_path_reports_success_failure_and_unavailable_clipboard_without_throwing()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(new SyntheticGameFile("Root/entry.bin")));
        vm.FocusedRow = vm.Rows[0];
        string? copied = null;
        await vm.CopyFocusedPathAsync(path => { copied = path; return Task.CompletedTask; });
        Assert.Equal("Root/entry.bin", copied);
        Assert.Equal(Loc.Instance["Browser.CopyPath.Success"], vm.ClipboardStatus);
        await vm.CopyFocusedPathAsync(_ => Task.FromException(new InvalidOperationException("fixture")));
        Assert.Equal(Loc.Instance["Browser.CopyPath.Failed"], vm.ClipboardStatus);
        await vm.CopyFocusedPathAsync(null);
        Assert.Equal(Loc.Instance["Browser.CopyPath.Failed"], vm.ClipboardStatus);
        vm.FocusedRow = null;
        Assert.Empty(vm.ClipboardStatus);
        await vm.CopyFocusedPathAsync(_ => throw new InvalidOperationException("must not be invoked"));
        Assert.Empty(vm.ClipboardStatus);
    }

    [Fact]
    public async Task Delayed_copy_does_not_overwrite_the_new_focused_entries_status()
    {
        using var vm = new BrowserViewModel();
        vm.Load(Scan(new SyntheticGameFile("Root/a.bin"), new SyntheticGameFile("Root/b.bin")));
        vm.FocusedRow = vm.Rows[0];
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copy = vm.CopyFocusedPathAsync(_ => completion.Task);
        vm.FocusedRow = vm.Rows[1];
        completion.SetResult();
        await copy;
        Assert.Empty(vm.ClipboardStatus);
    }
}
