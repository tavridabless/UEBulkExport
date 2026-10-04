using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.FileProvider.Objects;
using UEBulkExport.Gui.Converters;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

/// <summary>A folder inside the container. Children are built once from the flat path list.</summary>
public sealed partial class FolderNode : ObservableObject
{
    public string Name { get; }
    public string Path { get; }
    public FolderNode? Parent { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public List<GameFile> Files { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    private int _totalCount = -1;
    private long _totalBytes = -1;

    public FolderNode(string name, string path, FolderNode? parent)
    {
        Name = name;
        Path = path;
        Parent = parent;
    }

    /// <summary>Entries in this folder and everything below it.</summary>
    public int TotalCount => _totalCount >= 0 ? _totalCount : _totalCount = Files.Count + Children.Sum(c => c.TotalCount);
    public long TotalBytes => _totalBytes >= 0 ? _totalBytes : _totalBytes = Files.Sum(f => f.Size) + Children.Sum(c => c.TotalBytes);

    public IEnumerable<GameFile> AllFiles => Files.Concat(Children.SelectMany(c => c.AllFiles));
}

/// <summary>One line in the file list: a sub-folder or a file in the current search scope.</summary>
public sealed partial class BrowserRow : ObservableObject
{
    private readonly BrowserViewModel _owner;
    private bool _syncing;

    public FolderNode? Folder { get; }
    public GameFile? File { get; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isExcluded;

    public BrowserRow(BrowserViewModel owner, FolderNode folder)
    {
        _owner = owner;
        Folder = folder;
    }

    public BrowserRow(BrowserViewModel owner, GameFile file)
    {
        _owner = owner;
        File = file;
    }

    public bool IsFolder => Folder is not null;
    public string Name => Folder?.Name ?? File!.Name;
    public string Path => Folder?.Path ?? File!.Path;
    public string TypeText => Folder is not null ? Loc.Instance["Browser.Kind.Folder"] : File!.Extension.ToLowerInvariant();
    public long Size => Folder?.TotalBytes ?? File!.Size;
    public string SizeText => Folder is not null
        ? Loc.Instance.Format("Browser.Details.Folder", Folder.TotalCount, Format.Bytes(Folder.TotalBytes))
        : Format.Bytes(File!.Size);

    /// <summary>Updates the box without echoing the change back to the selection sets.</summary>
    public void Sync(bool isChecked, bool isExcluded)
    {
        _syncing = true;
        IsChecked = isChecked;
        IsExcluded = isExcluded;
        _syncing = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_syncing) _owner.SetChecked(this, value);
    }

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(SizeText));
    }
}

public sealed record FilterChip(string Key, string LabelKey)
{
    public string Label => Loc.Instance[LabelKey];
}

public sealed record ExtensionRow(string Extension, int Count, long Bytes)
{
    public string BytesText => Format.Bytes(Bytes);
}

public sealed record ContainerRow(string Name, int FileCount, bool IsLocked)
{
    public string Detail => IsLocked ? Loc.Instance["Browser.Locked"] : $"{FileCount:N0}";
}

public enum BrowserSortField { Name, Type, Size }

/// <summary>
/// The container browser: a folder tree, paged folder/subtree rows with tick boxes, details for
/// the focused row, and the selection that becomes the export. Ticked entries are exported; if
/// nothing is ticked, everything except the exclusion list is.
/// </summary>
public sealed partial class BrowserViewModel : ObservableObject, IDisposable
{
    private const int MaxRows = 5000;
    private static Loc L => Loc.Instance;

    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GameFile> _all = [];
    private int _pageIndex;
    private int _matchingRowCount;
    private int _matchingFileCount;
    private long _matchingBytes;
    private long _totalBytes;
    // Only the current scope is cached. Searching/changing type filters scans this array once,
    // without re-walking the tree or re-sorting on every keystroke.
    private GameFile[] _scopeFiles = [];
    private FolderNode[] _scopeFolders = [];
    private FolderNode? _cachedScopeFolder;
    private bool _cachedScopeIncludesSubfolders;
    private bool _scopeDirty = true;
    private bool _changingSort;

    public ObservableCollection<FolderNode> Roots { get; } = [];
    public ObservableCollection<FolderNode> Breadcrumb { get; } = [];
    private readonly BatchObservableCollection<BrowserRow> _rows = [];
    public ObservableCollection<BrowserRow> Rows => _rows;
    public ObservableCollection<ExtensionRow> Extensions { get; } = [];
    public ObservableCollection<ContainerRow> Containers { get; } = [];

    public IReadOnlyList<FilterChip> Chips { get; } =
    [
        new("all", "Browser.Chip.All"),
        new("packages", "Browser.Chip.Packages"),
        new("maps", "Browser.Chip.Maps"),
        new("media", "Browser.Chip.Media"),
        new("config", "Browser.Chip.Config"),
        new("other", "Browser.Chip.Other")
    ];

    [ObservableProperty] private FolderNode? _selectedFolder;
    [ObservableProperty] private BrowserRow? _focusedRow;
    [ObservableProperty] private FilterChip _chip;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _includeSubfolders;
    [ObservableProperty] private BrowserSortField _sortField;
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private string _clipboardStatus = "";
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _totalSummary = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _excludedCount;

    /// <summary>The export form, for the destination shown in the details panel.</summary>
    public ExportViewModel? Export { get; set; }

    /// <summary>Raised by the export button: the paths to export, or null for the whole container.</summary>
    public event Action<IReadOnlySet<string>?>? ExportRequested;

    public bool HasSelection => SelectedCount > 0 || ExcludedCount > 0;
    public string SelectedCountText => L.Format("Browser.SelectedCount", SelectedCount);
    public string SelectedSubText => L.Format("Browser.SelectedSub", ExcludedCount);
    public string ExportButtonText => SelectedCount > 0 ? L["Browser.ExportSelected"] : L["Browser.ExportAll"];
    public int MatchingRowCount => _matchingRowCount;
    public int PageNumber => _pageIndex + 1;
    public int PageCount => Math.Max(1, (_matchingRowCount - 1) / MaxRows + 1);
    public bool HasPreviousPage => _pageIndex > 0;
    public bool HasNextPage => (long)(_pageIndex + 1) * MaxRows < _matchingRowCount;
    public string PagingSummary => L.Format("Browser.Paging.Summary", Rows.Count, MatchingRowCount, PageNumber, PageCount);
    public int MatchingFileCount => _matchingFileCount;
    public long MatchingBytes => _matchingBytes;
    public bool HasFileMatches => MatchingFileCount > 0;
    public string MatchesSummary => L.Format("Browser.Matches.Summary", MatchingFileCount, Format.Bytes(MatchingBytes));
    public string VisibleSelectionSummary => L.Format("Browser.Selection.Visible",
        Rows.Count(r => r.IsChecked), Rows.Count(r => r.IsExcluded));
    public string NameSortHeader => SortHeader("Browser.Column.Name", BrowserSortField.Name);
    public string TypeSortHeader => SortHeader("Browser.Column.Type", BrowserSortField.Type);
    public string SizeSortHeader => SortHeader("Browser.Column.Size", BrowserSortField.Size);

    private string SortHeader(string key, BrowserSortField field) =>
        L[key] + (SortField == field ? SortDescending ? " ↓" : " ↑" : "");

    // ---------------------------------------------------------------- details

    public bool HasDetail => FocusedRow is not null;
    public string DetailName => FocusedRow?.Name ?? "";
    public string DetailKind => FocusedRow switch
    {
        { IsFolder: true } => L["Browser.Kind.Folder"],
        { File: { IsUePackage: true } f } => f.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase)
            ? L["Browser.Kind.Map"] : L["Browser.Kind.Package"],
        { File: { IsUePackagePayload: true } } => L["Browser.Kind.Payload"],
        { File: not null } => L["Browser.Kind.Loose"],
        _ => ""
    };
    public string DetailSize => FocusedRow?.SizeText ?? "";
    public string DetailExtension => FocusedRow?.File is { } f ? "." + f.Extension.ToLowerInvariant() : "";
    public string DetailPath => FocusedRow?.Path ?? "";
    public string DetailContainer => ContainerOf(FocusedRow?.File) ?? (Containers.Count == 1 ? Containers[0].Name : "");

    /// <summary>Pak and IoStore entries both expose their reader as "Vfs"; resolved by name to stay off the concrete types.</summary>
    private static string? ContainerOf(GameFile? file)
    {
        var vfs = file?.GetType().GetProperty("Vfs")?.GetValue(file);
        return vfs?.GetType().GetProperty("Name")?.GetValue(vfs) as string;
    }
    public string DetailEngine => Export?.SelectedGame.Label ?? "";
    public bool DetailHasExtension => !string.IsNullOrEmpty(DetailExtension);

    public BrowserViewModel()
    {
        _chip = Chips[0];
        Loc.Instance.LanguageChanged += RefreshLanguage;
    }

    // ---------------------------------------------------------------- loading

    public void Load(ScanResult scan)
    {
        Clear();
        _all = scan.Files;

        foreach (var c in scan.Containers.OrderBy(c => c.IsLocked).ThenBy(c => c.Name))
            Containers.Add(new ContainerRow(c.Name, c.FileCount, c.IsLocked));

        var listing = BulkExporter.GetListing(scan.Files);
        _totalBytes = listing.TotalBytes;
        foreach (var g in listing.ByExtension)
            Extensions.Add(new ExtensionRow(g.Extension, g.Count, g.Bytes));

        foreach (var root in BuildTree(scan.Files)) Roots.Add(root);
        if (Roots.Count > 0) Roots[0].IsExpanded = true;

        TotalSummary = L.Format("Browser.Total", listing.TotalCount, Format.Bytes(listing.TotalBytes));
        HasData = true;
        SelectedFolder = Roots.FirstOrDefault();
    }

    public void Clear()
    {
        Roots.Clear();
        Rows.Clear();
        Breadcrumb.Clear();
        Extensions.Clear();
        Containers.Clear();
        _selected.Clear();
        _excluded.Clear();
        SelectedCount = ExcludedCount = 0;
        _all = [];
        _totalBytes = 0;
        _pageIndex = _matchingRowCount = 0;
        _matchingFileCount = 0;
        _matchingBytes = 0;
        _scopeFiles = [];
        _scopeFolders = [];
        _cachedScopeFolder = null;
        _scopeDirty = true;
        ClipboardStatus = "";
        HasData = false;
        SelectedFolder = null;
        FocusedRow = null;
        TotalSummary = "";
        RefreshSelectionTexts();
        NotifyPaging();
    }

    private static List<FolderNode> BuildTree(IReadOnlyList<GameFile> files)
    {
        var roots = new List<FolderNode>();
        var nodes = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var separator = file.Path.LastIndexOf('/');
            var folderPath = separator < 0 ? file.Path : file.Path[..separator];
            if (!nodes.TryGetValue(folderPath, out var node))
            {
                var segments = folderPath.Split('/');
                FolderNode? parent = null;
                foreach (var segment in segments)
                {
                    var path = parent is null ? segment : parent.Path + "/" + segment;
                    if (!nodes.TryGetValue(path, out node))
                    {
                        nodes[path] = node = new FolderNode(segment, path, parent);
                        if (parent is null) roots.Add(node);
                        else parent.Children.Add(node);
                    }
                    parent = node;
                }
            }

            node!.Files.Add(file);
        }

        SortChildren(roots);
        return roots.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void SortChildren(IEnumerable<FolderNode> nodes)
    {
        foreach (var node in nodes)
        {
            // Scan data is immutable for this browser session. Sort once, not on every keystroke.
            var sortedFiles = node.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            node.Files.Clear();
            node.Files.AddRange(sortedFiles);
            var sorted = node.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            node.Children.Clear();
            foreach (var c in sorted) node.Children.Add(c);
            SortChildren(node.Children);
        }
    }

    // ---------------------------------------------------------------- navigation

    partial void OnSelectedFolderChanged(FolderNode? value)
    {
        _scopeDirty = true;
        FocusedRow = null;
        Breadcrumb.Clear();
        for (var n = value; n is not null; n = n.Parent) Breadcrumb.Insert(0, n);
        for (var n = value?.Parent; n is not null; n = n.Parent) n.IsExpanded = true;
        RefreshRows();
    }

    partial void OnSearchChanged(string value) => RefreshRows();
    partial void OnChipChanged(FilterChip value) => RefreshRows();
    partial void OnIncludeSubfoldersChanged(bool value) { _scopeDirty = true; RefreshRows(); }
    partial void OnSortFieldChanged(BrowserSortField value) => RefreshSort();
    partial void OnSortDescendingChanged(bool value) => RefreshSort();
    partial void OnFocusedRowChanged(BrowserRow? value) { ClipboardStatus = ""; RefreshDetails(); }

    [RelayCommand] private void SortByName() => ChangeSort(BrowserSortField.Name);
    [RelayCommand] private void SortByType() => ChangeSort(BrowserSortField.Type);
    [RelayCommand] private void SortBySize() => ChangeSort(BrowserSortField.Size);

    private void ChangeSort(BrowserSortField field)
    {
        if (SortField == field) SortDescending = !SortDescending;
        else
        {
            // Use generated setters, but coalesce the two changes into one rows refresh.
            _changingSort = true;
            try { SortField = field; SortDescending = false; }
            finally { _changingSort = false; }
            RefreshSort();
        }
    }

    private void RefreshSort()
    {
        _scopeDirty = true;
        if (_changingSort) return;
        RefreshRows();
        NotifySort();
    }

    private void NotifySort()
    {
        OnPropertyChanged(nameof(NameSortHeader));
        OnPropertyChanged(nameof(TypeSortHeader));
        OnPropertyChanged(nameof(SizeSortHeader));
    }

    [RelayCommand]
    private void NavigateTo(FolderNode? node)
    {
        if (node is not null) SelectedFolder = node;
    }

    [RelayCommand]
    private void OpenRow(BrowserRow? row)
    {
        if (row?.Folder is not null) SelectedFolder = row.Folder;
    }

    private static string CategoryOf(GameFile f)
    {
        var ext = f.Extension.ToLowerInvariant();
        if (f.IsUePackage) return ext == "umap" ? "maps" : "packages";
        if (f.IsUePackagePayload) return "packages";
        return ext switch
        {
            "png" or "jpg" or "jpeg" or "tga" or "bmp" or "mp4" or "bk2" or "ttf" or "otf" or "wav" or "ogg" or "binka" or "svg" => "media",
            "ini" or "json" or "txt" or "locres" or "locmeta" or "uplugin" or "uproject" or "csv" or "xml" => "config",
            _ => "other"
        };
    }

    private void RefreshRows(bool resetPage = true)
    {
        if (resetPage) _pageIndex = 0;
        _matchingRowCount = 0;
        _matchingFileCount = 0;
        _matchingBytes = 0;
        var focusPath = FocusedRow?.Path;
        var focusIsFolder = FocusedRow?.IsFolder;
        if (SelectedFolder is null) { FocusedRow = null; _rows.Clear(); NotifyPaging(); return; }
        EnsureScope();
        var rows = new List<BrowserRow>(Math.Min(MaxRows, _scopeFiles.Length + _scopeFolders.Length));

        var search = Search.Trim();
        var first = (long)_pageIndex * MaxRows;

        bool OnPage() => _matchingRowCount >= first && _matchingRowCount < first + MaxRows;

        foreach (var child in _scopeFolders)
        {
            if (search.Length > 0 && !child.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (OnPage()) rows.Add(new BrowserRow(this, child));
            _matchingRowCount++;
        }

        foreach (var f in _scopeFiles)
        {
            if (!MatchesFile(f, search)) continue;
            if (OnPage()) rows.Add(new BrowserRow(this, f));
            _matchingRowCount++;
            _matchingFileCount++;
            _matchingBytes += f.Size;
        }

        _rows.ReplaceWith(rows);
        SyncRows();
        FocusedRow = focusPath is null ? null : rows.FirstOrDefault(r => r.IsFolder == focusIsFolder &&
            string.Equals(r.Path, focusPath, StringComparison.Ordinal));
        NotifyPaging();
    }

    private bool MatchesFile(GameFile file, string search) =>
        (Chip.Key == "all" || CategoryOf(file) == Chip.Key) &&
        (search.Length == 0 || (IncludeSubfolders ? file.Path : file.Name)
            .Contains(search, StringComparison.OrdinalIgnoreCase));

    private void EnsureScope()
    {
        if (!_scopeDirty || SelectedFolder is null) return;
        if (!ReferenceEquals(_cachedScopeFolder, SelectedFolder) ||
            _cachedScopeIncludesSubfolders != IncludeSubfolders)
        {
            _scopeFolders = IncludeSubfolders ? [] : SelectedFolder.Children.ToArray();
            if (IncludeSubfolders)
            {
                var files = new List<GameFile>();
                var folders = new Stack<FolderNode>();
                folders.Push(SelectedFolder);
                while (folders.TryPop(out var folder))
                {
                    files.AddRange(folder.Files);
                    foreach (var child in folder.Children) folders.Push(child);
                }
                _scopeFiles = files.ToArray();
            }
            else _scopeFiles = SelectedFolder.Files.ToArray();
            _cachedScopeFolder = SelectedFolder;
            _cachedScopeIncludesSubfolders = IncludeSubfolders;
        }
        // Column/order changes sort the same reference array: no duplicate subtree traversal.
        Array.Sort(_scopeFolders, CompareFolders);
        Array.Sort(_scopeFiles, CompareFiles);
        _scopeDirty = false;
    }

    private int CompareFiles(GameFile left, GameFile right)
    {
        var primary = SortField switch
        {
            BrowserSortField.Size => left.Size.CompareTo(right.Size),
            BrowserSortField.Type => StringComparer.OrdinalIgnoreCase.Compare(left.Extension, right.Extension),
            _ => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name)
        };
        if (primary != 0) return SortDescending ? -primary : primary;
        var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return name != 0 ? name : StringComparer.Ordinal.Compare(left.Path, right.Path);
    }

    private int CompareFolders(FolderNode left, FolderNode right)
    {
        var primary = SortField == BrowserSortField.Size
            ? left.TotalBytes.CompareTo(right.TotalBytes)
            : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        if (primary != 0) return SortDescending ? -primary : primary;
        return StringComparer.Ordinal.Compare(left.Path, right.Path);
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private void NextPage()
    {
        if (!HasNextPage) return;
        _pageIndex++;
        RefreshRows(resetPage: false);
    }

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private void PreviousPage()
    {
        if (!HasPreviousPage) return;
        _pageIndex--;
        RefreshRows(resetPage: false);
    }

    private void NotifyPaging()
    {
        OnPropertyChanged(nameof(MatchingRowCount));
        OnPropertyChanged(nameof(PageNumber));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(PagingSummary));
        OnPropertyChanged(nameof(MatchingFileCount));
        OnPropertyChanged(nameof(MatchingBytes));
        OnPropertyChanged(nameof(HasFileMatches));
        OnPropertyChanged(nameof(MatchesSummary));
        OnPropertyChanged(nameof(VisibleSelectionSummary));
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        SelectAllMatchesCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- selection

    internal void SetChecked(BrowserRow row, bool value)
    {
        foreach (var path in PathsOf(row))
        {
            if (value) { _selected.Add(path); _excluded.Remove(path); }
            else _selected.Remove(path);
        }

        AfterSelectionChanged();
    }

    private static IEnumerable<string> PathsOf(BrowserRow row) =>
        row.Folder is not null ? row.Folder.AllFiles.Select(f => f.Path) : [row.File!.Path];

    private void SyncRows()
    {
        foreach (var row in Rows)
        {
            if (row.Folder is { } folder)
            {
                if (_selected.Count == 0 && _excluded.Count == 0) { row.Sync(false, false); continue; }
                var selected = folder.TotalCount > 0 && _selected.Count > 0;
                var excluded = folder.TotalCount > 0 && _excluded.Count > 0;
                foreach (var file in folder.AllFiles)
                {
                    selected &= _selected.Contains(file.Path);
                    excluded &= _excluded.Contains(file.Path);
                    if (!selected && !excluded) break;
                }
                row.Sync(selected, excluded);
            }
            else
            {
                row.Sync(_selected.Contains(row.Path), _excluded.Contains(row.Path));
            }
        }
    }

    private void AfterSelectionChanged()
    {
        SelectedCount = _selected.Count;
        ExcludedCount = _excluded.Count;
        SyncRows();
        RefreshSelectionTexts();
    }

    private void RefreshSelectionTexts()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectedSubText));
        OnPropertyChanged(nameof(ExportButtonText));
        OnPropertyChanged(nameof(VisibleSelectionSummary));
    }

    private void RefreshDetails()
    {
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailName));
        OnPropertyChanged(nameof(DetailKind));
        OnPropertyChanged(nameof(DetailSize));
        OnPropertyChanged(nameof(DetailExtension));
        OnPropertyChanged(nameof(DetailHasExtension));
        OnPropertyChanged(nameof(DetailPath));
        OnPropertyChanged(nameof(DetailContainer));
        OnPropertyChanged(nameof(DetailEngine));
    }

    [RelayCommand]
    private void SelectAllShown()
    {
        foreach (var row in Rows)
            foreach (var path in PathsOf(row)) { _selected.Add(path); _excluded.Remove(path); }
        AfterSelectionChanged();
    }

    /// <summary>Select matching files across every page, not entire matching navigation folders.</summary>
    [RelayCommand(CanExecute = nameof(HasFileMatches))]
    private void SelectAllMatches()
    {
        EnsureScope();
        var search = Search.Trim();
        foreach (var file in _scopeFiles)
            if (MatchesFile(file, search)) { _selected.Add(file.Path); _excluded.Remove(file.Path); }
        AfterSelectionChanged();
    }

    internal async Task CopyFocusedPathAsync(Func<string, Task>? copy)
    {
        var path = DetailPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (copy is null)
            {
                ClipboardStatus = L["Browser.CopyPath.Failed"];
                return;
            }
            await copy(path);
            // Completion of an old request must not overwrite a newly focused entry's status.
            if (DetailPath == path) ClipboardStatus = L["Browser.CopyPath.Success"];
        }
        catch (Exception exception)
        {
            Log.Warn($"could not copy browser path: {exception.Message}");
            if (DetailPath == path) ClipboardStatus = L["Browser.CopyPath.Failed"];
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _selected.Clear();
        _excluded.Clear();
        AfterSelectionChanged();
    }

    /// <summary>Ticked rows move to the exclusion list, so "everything but these" is one gesture.</summary>
    [RelayCommand]
    private void ExcludeChecked()
    {
        foreach (var path in _selected) _excluded.Add(path);
        _selected.Clear();
        AfterSelectionChanged();
    }

    [RelayCommand]
    private void ExportSelected()
    {
        IReadOnlySet<string>? paths = null;

        if (_selected.Count > 0)
            paths = _selected.Where(p => !_excluded.Contains(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        else if (_excluded.Count > 0)
            paths = _all.Select(f => f.Path).Where(p => !_excluded.Contains(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        ExportRequested?.Invoke(paths);
    }

    [RelayCommand]
    private void BrowseOutput() => Export?.BrowseOutputCommand.Execute(null);

    private void RefreshLanguage()
    {
        ClipboardStatus = "";
        OnPropertyChanged(nameof(Chips));
        foreach (var row in Rows) row.RefreshLanguage();
        TotalSummary = HasData ? L.Format("Browser.Total", _all.Count, Format.Bytes(_totalBytes)) : "";
        RefreshSelectionTexts();
        RefreshDetails();
        OnPropertyChanged(nameof(PagingSummary));
        OnPropertyChanged(nameof(MatchesSummary));
        NotifySort();
    }

    public void Dispose() => Loc.Instance.LanguageChanged -= RefreshLanguage;
}
