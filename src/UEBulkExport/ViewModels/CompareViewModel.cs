using CUE4Parse.UE4.Versions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Converters;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed record CompareGameOption(EGame? Game, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class CompareInputViewModel : ObservableObject
{
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _aes = "";
    [ObservableProperty] private CompareGameOption _game;
    [ObservableProperty] private bool _allowEngineSelection = true;

    public IReadOnlyList<CompareGameOption> Games { get; }

    public CompareInputViewModel(IReadOnlyList<CompareGameOption> games)
    {
        Games = games;
        _game = games[0];
    }

    public CompareSource GetSource() => new(Path.Trim(), Game.Game,
        Aes.Split(['\r', '\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (await DialogService.PickFolderAsync("Compare.PickFolder", Path) is { } path) Path = path;
    }

    [RelayCommand]
    private async Task BrowseContainer()
    {
        if (await DialogService.PickContainerAsync(Path) is { } path) Path = path;
    }
}

public sealed partial class CompareRow(CompareGroup group, Action<CompareRow, bool> onSelectionChanged) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    public CompareGroup Group { get; } = group;
    public string Path => Group.Path;
    public string Change => Loc.Instance[$"Compare.Change.{Group.Change}"];
    public string Detail => Group.Kind == CompareGroupKind.File ? FormatSizes(Group.Changes[0]) : FormatCauses();

    partial void OnIsSelectedChanged(bool value) => onSelectionChanged(this, value);

    private static string FormatSizes(CompareEntry entry) =>
        $"{(entry.BeforeSize is { } before ? Format.Bytes(before) : "—")} → " +
        $"{(entry.AfterSize is { } after ? Format.Bytes(after) : "—")}";

    private string FormatCauses()
    {
        var names = Group.Changes.Take(3).Select(entry => System.IO.Path.GetFileName(entry.Path));
        var text = string.Join(", ", names);
        return Group.Changes.Count > 3 ? $"{text} …" : text;
    }
}

public sealed record CompareFilter(CompareChange? Change)
{
    public string Label => Loc.Instance[Change is { } change ? $"Compare.Change.{change}" : "Compare.Filter.All"];
}

public sealed partial class CompareViewModel : ObservableObject, IDisposable
{
    private static Loc L => Loc.Instance;
    private readonly AppSettings _settings;
    private readonly Func<bool> _exportAvailable;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _filterCancellation;
    private CompareResult? _result;
    private CompareSource? _afterSource;
    private CompareRow[] _allRows = [];
    private readonly Dictionary<string, CompareRow> _selected = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressSelectionUpdates;
    private int _visibleSelected;
    private string _appliedSearch = "";
    private CompareChange? _appliedChange;
    private bool _disposed;

    public CompareInputViewModel Before { get; }
    public CompareInputViewModel After { get; }
    public IReadOnlyList<CompareFilter> Filters { get; } = [new(null),
        new(CompareChange.Added), new(CompareChange.Changed), new(CompareChange.Removed)];
    public event Action<CompareResult, CompareSource, IReadOnlySet<string>>? ReviewExportRequested;

    [ObservableProperty] private bool _hashContents;
    [ObservableProperty] private bool _inputsExpanded = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSavingManifest;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private CompareFilter _filter;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _manifestMessage = "";
    [ObservableProperty] private CompareManifest? _pendingManifest;
    [ObservableProperty] private bool _wasCancelled;
    [ObservableProperty] private IReadOnlyList<CompareRow> _rows = [];

    public bool CanEdit => !_disposed && !IsBusy;
    public bool CanRun => CanEdit && !string.IsNullOrWhiteSpace(Before.Path) && !string.IsNullOrWhiteSpace(After.Path);
    public bool CanCancel => !_disposed && IsBusy && _cancellation is { IsCancellationRequested: false };
    public bool HasResult => _result is not null;
    public bool HasError => Error.Length > 0;
    public bool HasRows => Rows.Count > 0;
    public bool ShowEmptyResults => HasResult && !HasRows;
    public bool ShowExportLimit => _result is { CanExportAfter: false, Added: > 0 } or
        { CanExportAfter: false, Changed: > 0 };
    public bool CanReview => CanEdit && _exportAvailable() && _result is { CanExportAfter: true } &&
        GroupsToUse().Any(row => row.Group.AfterPaths.Count > 0);
    public bool CanSaveManifest => CanEdit && _allRows.Length > 0;
    public bool HasChecked => _selected.Count > 0;
    public bool CanChangeHash => CanEdit && PendingManifest is null;
    public bool HasPendingManifest => PendingManifest is not null;
    public bool HasManifestMessage => ManifestMessage.Length > 0;

    public string Summary { get; private set; } = "";
    public string CheckSummary => L.Format("Compare.Selection.Count", _selected.Count, _selected.Count - _visibleSelected);
    public string ReviewLabel => L[HasChecked ? "Compare.ExportSelection" : "Compare.ExportAll"];
    public string RunLabel => L[HasPendingManifest ? "Compare.Manifest.Verify" : "Compare.Run"];

    public string StatusText => IsBusy ? L[IsSavingManifest ? "Compare.Manifest.Saving" :
        HasPendingManifest ? "Compare.Manifest.Verifying" : "Compare.Running"] :
        WasCancelled ? L["Compare.Cancelled"] : Summary;

    public CompareViewModel(AppSettings settings, IReadOnlyList<GameOption> games, Func<bool> exportAvailable)
    {
        _settings = settings;
        _exportAvailable = exportAvailable;
        var available = new List<CompareGameOption> { new(null, L["Compare.Engine.Auto"]) };
        available.AddRange(games.Select(game => new CompareGameOption(game.Value, game.Label)));
        Before = new(available);
        After = new(available);
        _filter = Filters[0];
        Before.PropertyChanged += InputChanged;
        After.PropertyChanged += InputChanged;
    }

    private void InputChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CompareInputViewModel.AllowEngineSelection)) Invalidate();
    }

    partial void OnHashContentsChanged(bool value) => Invalidate();
    partial void OnSearchChanged(string value) => _ = UpdateRowsAsync();
    partial void OnFilterChanged(CompareFilter value) => _ = UpdateRowsAsync();
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnManifestMessageChanged(string value) => OnPropertyChanged(nameof(HasManifestMessage));
    partial void OnWasCancelledChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnPendingManifestChanged(CompareManifest? value)
    {
        Before.AllowEngineSelection = After.AllowEngineSelection = value is null;
        OnPropertyChanged(nameof(HasPendingManifest));
        OnPropertyChanged(nameof(CanChangeHash));
        OnPropertyChanged(nameof(RunLabel));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanChangeHash));
        OnPropertyChanged(nameof(StatusText));
        CompareCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        LoadManifestCommand.NotifyCanExecuteChanged();
        SaveManifestCommand.NotifyCanExecuteChanged();
        RefreshExportAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Compare()
    {
        if (!CanRun) return;
        var options = new CompareOptions(Before.GetSource(), After.GetSource(), HashContents,
            UnsetIfEmpty(_settings.OodlePath), UnsetIfEmpty(_settings.ZlibPath));
        var manifest = PendingManifest;
        var scope = manifest?.Groups.Select(group => group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        ClearResult();
        Progress = 0;

        try
        {
            var progress = new Progress<double>(value =>
            {
                if (!_disposed && ReferenceEquals(_cancellation, cancellation)) Progress = value;
            });
            var token = cancellation.Token;
            var prepared = await Task.Run(() =>
            {
                var result = manifest is null ? CompareBuilds.Run(options, progress, token) :
                    manifest.Verify(options.Before, options.After, options.OodlePath, options.ZlibPath, progress, token).Result;
                var rows = result.Groups.Where(group => scope is null || scope.Contains(group.Id))
                    .Select(group => new CompareRow(group, ToggleSelection)).ToArray();
                return (result, rows);
            }, token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            _result = prepared.result;
            _allRows = prepared.rows;
            _afterSource = options.After;
            UpdateSummary();
            if (manifest is not null) ManifestMessage = L["Compare.Manifest.Verified"];
            InputsExpanded = false;
            RefreshResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed) WasCancelled = true;
        }
        catch (UserFacingException exception)
        {
            if (!_disposed) Error = string.Join(Environment.NewLine,
                new[] { exception.Headline, exception.Hint }.Where(text => !string.IsNullOrWhiteSpace(text)));
        }
        catch (Exception exception)
        {
            UEBulkExport.Log.Error(exception.ToString());
            if (!_disposed) Error = L["Error.Unexpected"];
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cancellation?.Cancel();
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task LoadManifest()
    {
        var path = await DialogService.PickCompareManifestAsync();
        if (path is null || !CanEdit) return;
        try
        {
            var manifest = await Task.Run(() => CompareManifest.Load(path));
            if (!CanEdit) return;
            PendingManifest = manifest;
            Invalidate();
            ManifestMessage = L["Compare.Manifest.Loaded"];
            InputsExpanded = true;
        }
        catch (UserFacingException e)
        {
            if (!_disposed) ManifestMessage = e.Headline;
        }
        catch (Exception e)
        {
            UEBulkExport.Log.Error(e.ToString());
            if (!_disposed) ManifestMessage = L["Error.Unexpected"];
        }
    }

    [RelayCommand]
    private void CloseManifest()
    {
        if (!CanEdit) return;
        PendingManifest = null;
        ManifestMessage = "";
        Invalidate();
    }

    [RelayCommand(CanExecute = nameof(CanSaveManifest))]
    private async Task SaveManifest()
    {
        if (!CanSaveManifest || _result is not { } result) return;
        var ids = GroupsToUse().Select(row => row.Group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var path = await DialogService.SaveCompareManifestAsync();
        if (path is null || !CanSaveManifest || !ReferenceEquals(_result, result)) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        Progress = 0;
        IsSavingManifest = true;
        IsBusy = true;
        try
        {
            await Task.Run(() => CompareManifest.Capture(result, ids, cancellation.Token).Save(path), cancellation.Token);
            if (!_disposed) ManifestMessage = L["Compare.Manifest.Saved"];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed) ManifestMessage = L["Compare.Cancelled"];
        }
        catch (UserFacingException e)
        {
            if (!_disposed) ManifestMessage = e.Headline;
        }
        catch (Exception e)
        {
            UEBulkExport.Log.Error(e.ToString());
            if (!_disposed) ManifestMessage = L["Error.Unexpected"];
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
            IsSavingManifest = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanReview))]
    private void Review()
    {
        if (!CanReview || _result is null || _afterSource is null) return;
        var paths = GroupsToUse().SelectMany(row => row.Group.AfterPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count > 0) ReviewExportRequested?.Invoke(_result, _afterSource, paths);
    }

    [RelayCommand]
    private void ClearChecks()
    {
        _suppressSelectionUpdates = true;
        foreach (var row in _selected.Values) row.IsSelected = false;
        _suppressSelectionUpdates = false;
        _selected.Clear();
        _visibleSelected = 0;
        RefreshSelection();
    }

    private IEnumerable<CompareRow> GroupsToUse() => HasChecked ? _selected.Values : _allRows;

    private void ToggleSelection(CompareRow row, bool isSelected)
    {
        if (_suppressSelectionUpdates) return;
        if (isSelected) _selected.Add(row.Group.Id, row);
        else _selected.Remove(row.Group.Id);
        if (Matches(row, _appliedSearch, _appliedChange)) _visibleSelected += isSelected ? 1 : -1;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(CheckSummary));
        OnPropertyChanged(nameof(ReviewLabel));
        RefreshExportAvailability();
    }

    internal void RefreshExportAvailability()
    {
        ReviewCommand.NotifyCanExecuteChanged();
    }

    private void Invalidate()
    {
        if (_disposed) return;
        if (IsBusy) Cancel();
        ClearResult();
        CompareCommand.NotifyCanExecuteChanged();
    }

    private void ClearResult()
    {
        _result = null;
        _allRows = [];
        ClearChecks();
        _afterSource = null;
        Error = "";
        WasCancelled = false;
        UpdateSummary();
        RefreshResult();
    }

    private void RefreshResult()
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowExportLimit));
        OnPropertyChanged(nameof(StatusText));
        _filterCancellation?.Cancel();
        RefreshRows(FilterRows(_allRows, Search.Trim(), Filter.Change, CancellationToken.None), Search.Trim(), Filter.Change);
        SaveManifestCommand.NotifyCanExecuteChanged();
        RefreshExportAvailability();
    }

    private void UpdateSummary()
    {
        var added = 0;
        var changed = 0;
        var removed = 0;
        foreach (var row in _allRows)
        {
            switch (row.Group.Change)
            {
                case CompareChange.Added: added++; break;
                case CompareChange.Changed: changed++; break;
                case CompareChange.Removed: removed++; break;
            }
        }
        Summary = _result is null ? "" : L.Format("Compare.Summary.Groups",
            added, changed, removed);
        OnPropertyChanged(nameof(Summary));
    }

    private async Task UpdateRowsAsync()
    {
        _filterCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;
        var ct = cancellation.Token;
        var search = Search.Trim();
        var change = Filter.Change;
        var rows = _allRows;
        try
        {
            await Task.Delay(140, ct);
            var filtered = await Task.Run(() => FilterRows(rows, search, change, ct), ct);
            if (!_disposed && !ct.IsCancellationRequested) RefreshRows(filtered, search, change);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_filterCancellation, cancellation)) _filterCancellation = null;
        }
    }

    private static CompareRow[] FilterRows(CompareRow[] rows, string search, CompareChange? change, CancellationToken ct)
    {
        if (search.Length == 0 && change is null) return rows;
        var filtered = new List<CompareRow>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (Matches(row, search, change)) filtered.Add(row);
        }
        return filtered.ToArray();
    }

    private static bool Matches(CompareRow row, string search, CompareChange? change) =>
        (change is null || row.Group.Change == change) &&
        (row.Path.Contains(search, StringComparison.OrdinalIgnoreCase) ||
         row.Group.Changes.Any(entry => entry.Path.Contains(search, StringComparison.OrdinalIgnoreCase)));

    private void RefreshRows(CompareRow[] rows, string search, CompareChange? change)
    {
        _appliedSearch = search;
        _appliedChange = change;
        _visibleSelected = _selected.Values.Count(row => Matches(row, search, change));
        Rows = rows;
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowEmptyResults));
        OnPropertyChanged(nameof(CheckSummary));
    }

    private static string? UnsetIfEmpty(string path) => string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    public void Dispose()
    {
        _disposed = true;
        Before.PropertyChanged -= InputChanged;
        After.PropertyChanged -= InputChanged;
        _cancellation?.Cancel();
        _filterCancellation?.Cancel();
    }
}
