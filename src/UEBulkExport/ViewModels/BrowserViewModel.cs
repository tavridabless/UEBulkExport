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

/// <summary>One line in the file list: a sub-folder or a file of the current folder.</summary>
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

/// <summary>
/// The container browser: a folder tree, the current folder's rows with tick boxes, details for
/// the focused row, and the selection that becomes the export. Ticked entries are exported; if
/// nothing is ticked, everything except the exclusion list is.
/// </summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    private const int MaxRows = 5000;
    private static Loc L => Loc.Instance;

    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GameFile> _all = [];

    public ObservableCollection<FolderNode> Roots { get; } = [];
    public ObservableCollection<FolderNode> Breadcrumb { get; } = [];
    public ObservableCollection<BrowserRow> Rows { get; } = [];
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
        Loc.Instance.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(Chips));
            RefreshSelectionTexts();
            RefreshDetails();
        };
    }

    // ---------------------------------------------------------------- loading

    public void Load(ScanResult scan)
    {
        Clear();
        _all = scan.Files;

        foreach (var c in scan.Containers.OrderBy(c => c.IsLocked).ThenBy(c => c.Name))
            Containers.Add(new ContainerRow(c.Name, c.FileCount, c.IsLocked));

        var listing = BulkExporter.GetListing(scan.Files);
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
        _all = [];
        HasData = false;
        SelectedFolder = null;
        FocusedRow = null;
        TotalSummary = "";
        RefreshSelectionTexts();
    }

    private static List<FolderNode> BuildTree(IReadOnlyList<GameFile> files)
    {
        var roots = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var segments = file.Path.Split('/');
            if (segments.Length == 0) continue;

            if (!roots.TryGetValue(segments[0], out var node))
                roots[segments[0]] = node = new FolderNode(segments[0], segments[0], null);

            for (var i = 1; i < segments.Length - 1; i++)
            {
                var child = node.Children.FirstOrDefault(c => c.Name.Equals(segments[i], StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = new FolderNode(segments[i], node.Path + "/" + segments[i], node);
                    node.Children.Add(child);
                }

                node = child;
            }

            node.Files.Add(file);
        }

        SortChildren(roots.Values);
        return roots.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void SortChildren(IEnumerable<FolderNode> nodes)
    {
        foreach (var node in nodes)
        {
            var sorted = node.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            node.Children.Clear();
            foreach (var c in sorted) node.Children.Add(c);
            SortChildren(node.Children);
        }
    }

    // ---------------------------------------------------------------- navigation

    partial void OnSelectedFolderChanged(FolderNode? value)
    {
        Breadcrumb.Clear();
        for (var n = value; n is not null; n = n.Parent) Breadcrumb.Insert(0, n);
        for (var n = value?.Parent; n is not null; n = n.Parent) n.IsExpanded = true;
        RefreshRows();
    }

    partial void OnSearchChanged(string value) => RefreshRows();
    partial void OnChipChanged(FilterChip value) => RefreshRows();
    partial void OnFocusedRowChanged(BrowserRow? value) => RefreshDetails();

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

    private void RefreshRows()
    {
        Rows.Clear();
        FocusedRow = null;
        if (SelectedFolder is null) return;

        var search = Search.Trim();
        var shown = 0;

        foreach (var child in SelectedFolder.Children)
        {
            if (search.Length > 0 && !child.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(new BrowserRow(this, child));
        }

        IEnumerable<GameFile> files = SelectedFolder.Files;
        if (Chip.Key != "all") files = files.Where(f => CategoryOf(f) == Chip.Key);
        if (search.Length > 0) files = files.Where(f => f.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        foreach (var f in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(new BrowserRow(this, f));
            if (++shown >= MaxRows) break;
        }

        SyncRows();
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
                var paths = folder.AllFiles.Select(f => f.Path).ToList();
                row.Sync(paths.Count > 0 && paths.All(_selected.Contains), paths.Count > 0 && paths.All(_excluded.Contains));
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
}
