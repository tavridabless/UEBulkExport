using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace UEBulkExport.Gui.Services;

/// <summary>
/// Collects log entries for the Log tab. Entries arrive from worker threads and are batched
/// onto the UI thread a few times a second, so a verbose export cannot freeze the window.
/// </summary>
public sealed class UiLogSink : ILogSink
{
    private const int MaxEntries = 20_000;

    private readonly Lock _gate = new();
    private readonly List<LogEntry> _pending = [];
    private bool _flushScheduled;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>Raised on the UI thread after a batch has been appended.</summary>
    public event Action? Appended;

    public void Write(in LogEntry entry)
    {
        // Blank spacer lines make sense on a console, not in a list.
        if (entry.Level == LogLevel.Raw && string.IsNullOrWhiteSpace(entry.Message)) return;

        lock (_gate)
        {
            _pending.Add(entry);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }

        DispatcherTimer.RunOnce(Flush, TimeSpan.FromMilliseconds(120), DispatcherPriority.Background);
    }

    private void Flush()
    {
        List<LogEntry> batch;
        lock (_gate)
        {
            batch = [.. _pending];
            _pending.Clear();
            _flushScheduled = false;
        }

        foreach (var entry in batch) Entries.Add(entry);

        // Drop the oldest lines rather than growing without bound over a long session.
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);

        Appended?.Invoke();
    }

    public void Clear()
    {
        lock (_gate) _pending.Clear();
        Entries.Clear();
    }
}
