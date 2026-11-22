using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Localization;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed partial class LogViewModel : ObservableObject, IDisposable
{
    private readonly UiLogSink _sink;

    private readonly BatchObservableCollection<LogEntry> _visible = [];
    public ObservableCollection<LogEntry> Visible => _visible;

    /// <summary>0 = everything, 1 = warnings and errors, 2 = errors only.</summary>
    [ObservableProperty] private int _filter;
    [ObservableProperty] private bool _autoScroll = true;

    /// <summary>Set by the view; scrolls the list to its last item.</summary>
    public Action? ScrollToEnd { get; set; }

    public bool IsEmpty => Visible.Count == 0;
    public string RetentionText => _sink.DroppedEntries > 0
        ? Loc.Instance.Format("Log.Retention", _sink.Entries.Count, _sink.DroppedEntries) : "";

    public LogViewModel(UiLogSink sink)
    {
        _sink = sink;
        _sink.BatchChanged += OnBatchChanged;
        _sink.Appended += NotifyRetention;
        Loc.Instance.LanguageChanged += NotifyRetention;
        Rebuild();
    }

    partial void OnFilterChanged(int value) => Rebuild();
    partial void OnAutoScrollChanged(bool value)
    {
        if (value) ScrollToEnd?.Invoke();
    }

    private bool Passes(LogEntry e) => Filter switch
    {
        1 => e.Level is LogLevel.Warn or LogLevel.Error or LogLevel.Problem,
        2 => e.Level is LogLevel.Error or LogLevel.Problem,
        _ => true
    };

    private void OnBatchChanged(IReadOnlyList<LogEntry> evicted, IReadOnlyList<LogEntry> appended)
    {
        var wasEmpty = IsEmpty;
        var changed = Filter == 0
            ? _visible.RemoveFirstAndAppend(evicted.Count, appended)
            : _visible.RemoveFirstAndAppend(evicted.Count(Passes), appended.Where(Passes));
        if (!changed) return;
        if (wasEmpty != IsEmpty) OnPropertyChanged(nameof(IsEmpty));
        if (AutoScroll) ScrollToEnd?.Invoke();
    }

    private void Rebuild()
    {
        _visible.ReplaceWith(_sink.Entries.Where(Passes));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void NotifyRetention() => OnPropertyChanged(nameof(RetentionText));

    public void Dispose()
    {
        _sink.BatchChanged -= OnBatchChanged;
        _sink.Appended -= NotifyRetention;
        Loc.Instance.LanguageChanged -= NotifyRetention;
        ScrollToEnd = null;
    }

    [RelayCommand]
    private void Clear() => _sink.Clear();

    public string AllText()
    {
        var sb = new StringBuilder();
        foreach (var entry in Visible) sb.AppendLine(entry.Format());
        return sb.ToString();
    }
}
