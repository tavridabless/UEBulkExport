using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace UEBulkExport.Gui.Services;

/// <summary>Publishes one reset per batch instead of thousands of per-row UI notifications.</summary>
internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> items)
    {
        var snapshot = items.ToArray();
        CheckReentrancy();
        var list = (List<T>)Items;
        list.Clear();
        list.AddRange(snapshot);
        NotifyReset();
    }

    public int AppendBounded(IReadOnlyList<T> batch, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (batch.Count == 0) return 0;
        CheckReentrancy();
        var list = (List<T>)Items;
        var removed = (int)Math.Max(0, (long)list.Count + batch.Count - capacity);
        if (batch.Count >= capacity)
        {
            list.Clear();
            for (var index = batch.Count - capacity; index < batch.Count; index++) list.Add(batch[index]);
        }
        else
        {
            if (removed > 0) list.RemoveRange(0, removed);
            for (var index = 0; index < batch.Count; index++) list.Add(batch[index]);
        }
        NotifyReset();
        return removed;
    }

    private void NotifyReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
