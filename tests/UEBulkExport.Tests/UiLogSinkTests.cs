using System.Collections.Concurrent;
using System.Collections.Specialized;
using UEBulkExport.Gui.Services;
using UEBulkExport.Gui.ViewModels;

namespace UEBulkExport.Tests;

public sealed class UiLogSinkTests
{
    private static LogEntry Entry(int index, LogLevel level = LogLevel.Info) =>
        new(DateTime.UnixEpoch, level, "Synthetic " + index);

    [Fact]
    public void A_blocked_ui_has_bounded_pending_memory_and_one_scheduled_flush()
    {
        var callbacks = new Queue<Action>();
        var sink = new UiLogSink(callbacks.Enqueue);
        using var vm = new LogViewModel(sink);
        var sinkNotifications = 0;
        var visibleNotifications = 0;
        var scrolls = 0;
        sink.Entries.CollectionChanged += (_, _) => sinkNotifications++;
        vm.Visible.CollectionChanged += (_, _) => visibleNotifications++;
        vm.ScrollToEnd = () => scrolls++;

        for (var index = 0; index < 100_000; index++) sink.Write(Entry(index));

        Assert.Single(callbacks);
        Assert.Equal(20_000, sink.PendingCount);
        Assert.Equal(80_000, sink.DroppedEntries);
        callbacks.Dequeue()();
        Assert.Equal(20_000, sink.Entries.Count);
        Assert.Equal(Entry(80_000), sink.Entries[0]);
        Assert.Equal(Entry(99_999), sink.Entries[^1]);
        Assert.Equal(sink.Entries, vm.Visible);
        Assert.Equal(1, sinkNotifications);
        Assert.Equal(1, visibleNotifications);
        Assert.Equal(1, scrolls);
        Assert.NotEmpty(vm.RetentionText);
    }

    [Fact]
    public void Retention_across_flushes_keeps_the_latest_order_and_filters_without_losing_data()
    {
        var callbacks = new Queue<Action>();
        var sink = new UiLogSink(callbacks.Enqueue);
        using var vm = new LogViewModel(sink);
        for (var index = 0; index < 19_999; index++) sink.Write(Entry(index));
        callbacks.Dequeue()();
        sink.Write(Entry(19_999, LogLevel.Warn));
        sink.Write(Entry(20_000, LogLevel.Error));
        sink.Write(Entry(20_001, LogLevel.Problem));
        callbacks.Dequeue()();
        Assert.Equal(20_000, sink.Entries.Count);
        Assert.Equal(Entry(2), sink.Entries[0]);
        Assert.Equal(2, sink.DroppedEntries);
        vm.Filter = 1;
        Assert.Equal([LogLevel.Warn, LogLevel.Error, LogLevel.Problem], vm.Visible.Select(e => e.Level));
        vm.Filter = 2;
        Assert.Equal([LogLevel.Error, LogLevel.Problem], vm.Visible.Select(e => e.Level));
        vm.Filter = 0;
        Assert.Equal(20_000, vm.Visible.Count);
    }

    [Fact]
    public void Clear_removes_pending_and_visible_entries_and_resets_retention()
    {
        var callbacks = new Queue<Action>();
        var sink = new UiLogSink(callbacks.Enqueue);
        using var vm = new LogViewModel(sink);
        for (var index = 0; index < 20_001; index++) sink.Write(Entry(index));
        vm.ClearCommand.Execute(null);
        Assert.Equal(0, sink.PendingCount);
        Assert.Equal(0, sink.DroppedEntries);
        Assert.Empty(vm.RetentionText);
        sink.Write(Entry(50));
        Assert.Single(callbacks);
        callbacks.Dequeue()();
        Assert.Equal(Entry(50), Assert.Single(vm.Visible));
    }

    [Fact]
    public void Concurrent_producers_remain_bounded_and_raw_spacers_are_ignored()
    {
        var callbacks = new ConcurrentQueue<Action>();
        var sink = new UiLogSink(callbacks.Enqueue);
        sink.Write(new LogEntry(DateTime.UnixEpoch, LogLevel.Raw, "\t "));
        Assert.Empty(callbacks);
        Parallel.For(0, 60_000, index => sink.Write(Entry(index)));
        Assert.Single(callbacks);
        Assert.Equal(20_000, sink.PendingCount);
        Assert.Equal(40_000, sink.DroppedEntries);
        Assert.True(callbacks.TryDequeue(out var flush));
        flush();
        Assert.Equal(20_000, sink.Entries.Count);
        Assert.Equal(20_000, sink.Entries.Select(e => e.Message).Distinct().Count());
    }

    [Fact]
    public void Failed_scheduling_can_retry_without_stranding_pending_entries()
    {
        var callbacks = new Queue<Action>();
        var calls = 0;
        var sink = new UiLogSink(action =>
        {
            if (++calls == 1) throw new InvalidOperationException("Synthetic scheduler failure");
            callbacks.Enqueue(action);
        });
        Assert.Throws<InvalidOperationException>(() => sink.Write(Entry(1)));
        sink.Write(Entry(2));
        callbacks.Dequeue()();
        Assert.Equal([Entry(1), Entry(2)], sink.Entries);
    }

    [Fact]
    public void Follow_disabled_and_disposal_prevent_scroll_or_late_visible_updates()
    {
        var callbacks = new Queue<Action>();
        var sink = new UiLogSink(callbacks.Enqueue);
        var vm = new LogViewModel(sink) { AutoScroll = false };
        var scrolls = 0;
        vm.ScrollToEnd = () => scrolls++;
        sink.Write(Entry(1));
        callbacks.Dequeue()();
        Assert.Equal(0, scrolls);
        Assert.Single(vm.Visible);
        vm.Dispose();
        sink.Write(Entry(2));
        callbacks.Dequeue()();
        Assert.Single(vm.Visible);
    }

    [Fact]
    public void Enabling_follow_scrolls_existing_records_without_waiting_for_another_log_entry()
    {
        var callbacks = new Queue<Action>();
        var sink = new UiLogSink(callbacks.Enqueue);
        using var vm = new LogViewModel(sink) { AutoScroll = false };
        sink.Write(Entry(1));
        callbacks.Dequeue()();
        var scrolls = 0;
        vm.ScrollToEnd = () => scrolls++;
        vm.AutoScroll = true;
        Assert.Equal(1, scrolls);
    }

    [Fact]
    public void Bounded_batch_collection_preserves_tail_and_notifies_once()
    {
        var collection = new BatchObservableCollection<int>();
        var changes = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => changes.Add(e.Action);
        Assert.Equal(2, collection.AppendBounded([0, 1, 2, 3, 4], 3));
        Assert.Equal([2, 3, 4], collection);
        Assert.Equal(1, collection.AppendBounded([5], 3));
        Assert.Equal([3, 4, 5], collection);
        collection.ReplaceWith(collection.Where(i => i != 4));
        Assert.Equal([3, 5], collection);
        Assert.Equal(3, changes.Count);
        Assert.All(changes, action => Assert.Equal(NotifyCollectionChangedAction.Reset, action));
    }
}
