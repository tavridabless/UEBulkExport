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
    private readonly Queue<LogEntry> _pending = new();
    private readonly BatchObservableCollection<LogEntry> _entries = [];
    private readonly Action<Action> _schedule;
    private bool _flushScheduled;
    private long _droppedEntries;

    public ObservableCollection<LogEntry> Entries => _entries;
    public long DroppedEntries { get { lock (_gate) return _droppedEntries; } }
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    public UiLogSink() : this(action =>
        DispatcherTimer.RunOnce(action, TimeSpan.FromMilliseconds(120), DispatcherPriority.Background)) { }

    internal UiLogSink(Action<Action> schedule) => _schedule = schedule;

    /// <summary>Raised on the UI thread after a batch has been appended.</summary>
    public event Action? Appended;

    public void Write(in LogEntry entry)
    {
        // Blank spacer lines make sense on a console, not in a list.
        if (entry.Level == LogLevel.Raw && string.IsNullOrWhiteSpace(entry.Message)) return;

        lock (_gate)
        {
            if (_pending.Count == MaxEntries)
            {
                _pending.Dequeue();
                _droppedEntries++;
            }
            _pending.Enqueue(entry);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }

        try { _schedule(Flush); }
        catch
        {
            lock (_gate) _flushScheduled = false;
            throw;
        }
    }

    private void Flush()
    {
        LogEntry[] batch;
        lock (_gate)
        {
            batch = [.. _pending];
            _pending.Clear();
            _flushScheduled = false;
        }

        var removed = _entries.AppendBounded(batch, MaxEntries);
        lock (_gate) _droppedEntries += removed;

        Appended?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _droppedEntries = 0;
        }
        Entries.Clear();
        Appended?.Invoke();
    }
}
