using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UEBulkExport.Gui.Services;

namespace UEBulkExport.Gui.ViewModels;

public sealed partial class LogViewModel : ObservableObject
{
    private readonly UiLogSink _sink;

    public ObservableCollection<LogEntry> Visible { get; } = [];

    /// <summary>0 = everything, 1 = warnings and errors, 2 = errors only.</summary>
    [ObservableProperty] private int _filter;
    [ObservableProperty] private bool _autoScroll = true;

    /// <summary>Set by the view; scrolls the list to its last item.</summary>
    public Action? ScrollToEnd { get; set; }

    public bool IsEmpty => Visible.Count == 0;

    public LogViewModel(UiLogSink sink)
    {
        _sink = sink;
        _sink.Entries.CollectionChanged += OnEntriesChanged;
        Rebuild();
    }

    partial void OnFilterChanged(int value) => Rebuild();

    private bool Passes(in LogEntry e) => Filter switch
    {
        1 => e.Level is LogLevel.Warn or LogLevel.Error or LogLevel.Problem,
        2 => e.Level is LogLevel.Error or LogLevel.Problem,
        _ => true
    };

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (LogEntry entry in e.NewItems)
                    if (Passes(entry)) Visible.Add(entry);
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (LogEntry entry in e.OldItems) Visible.Remove(entry);
                break;

            default:
                Rebuild();
                break;
        }

        OnPropertyChanged(nameof(IsEmpty));
        if (AutoScroll) ScrollToEnd?.Invoke();
    }

    private void Rebuild()
    {
        Visible.Clear();
        foreach (var entry in _sink.Entries)
            if (Passes(entry)) Visible.Add(entry);
        OnPropertyChanged(nameof(IsEmpty));
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
