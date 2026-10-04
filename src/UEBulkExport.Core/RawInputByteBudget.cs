namespace UEBulkExport;

/// <summary>
/// FIFO weighted gate for raw entry reads. Reservations describe metadata source sizes, not
/// resident memory. Oversized entries make progress by running alone; queued large entries cannot
/// be starved by smaller arrivals. Cancellation removes its waiter without blocking the queue.
/// </summary>
internal sealed class RawInputByteBudget
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private readonly long _capacity;
    private long _reserved, _peakReserved;
    private int _active, _peakActive, _unknownSizeEntries;
    private bool _exclusive;

    public RawInputByteBudget(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public RawInputBudgetState Snapshot()
    {
        lock (_sync)
            return new RawInputBudgetState(_capacity, _reserved, _peakReserved, _active,
                _peakActive, _waiters.Count)
            {
                UnknownSizeEntries = _unknownSizeEntries
            };
    }

    public ValueTask<Lease> AcquireAsync(long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return AcquireCore(bytes, unknownSize: false, cancellationToken);
    }

    /// <summary>
    /// A reader with unknown metadata size runs alone. It occupies the whole gate, but its
    /// unknown source size is not fabricated as a known byte count in the published snapshot.
    /// </summary>
    public ValueTask<Lease> AcquireUnknownSizeAsync(CancellationToken cancellationToken = default) =>
        AcquireCore(0, unknownSize: true, cancellationToken);

    private ValueTask<Lease> AcquireCore(long bytes, bool unknownSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_sync)
        {
            if (_waiters.Count == 0 && CanReserve(bytes, unknownSize))
                return ValueTask.FromResult(Reserve(bytes, unknownSize));

            waiter = new Waiter(this, bytes, unknownSize, cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);
            // Register under the lock so release cannot complete this waiter before its
            // registration is installed. A synchronously invoked callback is lock-reentrant.
            waiter.Registration = cancellationToken.UnsafeRegister(static state =>
            {
                var pending = (Waiter)state!;
                pending.Owner.Cancel(pending);
            }, waiter);
        }

        return waiter.WaitAsync();
    }

    private bool CanReserve(long bytes, bool unknownSize) =>
        _active == 0 || (!unknownSize && !_exclusive && bytes <= _capacity && _reserved <= _capacity - bytes);

    private Lease Reserve(long bytes, bool unknownSize)
    {
        _reserved += bytes; // CanReserve prevents overflow, including long.MaxValue oversize.
        _active++;
        _exclusive = unknownSize || bytes > _capacity;
        if (unknownSize) _unknownSizeEntries++;
        _peakReserved = Math.Max(_peakReserved, _reserved);
        _peakActive = Math.Max(_peakActive, _active);
        return new Lease(this, bytes);
    }

    private void Cancel(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Node is null) return; // A granted lease belongs to its caller now.
            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            waiter.Completion.TrySetCanceled(waiter.Token);
            Drain();
        }
    }

    private void Release(long bytes)
    {
        lock (_sync)
        {
            _reserved -= bytes;
            _active--;
            if (_active == 0) _exclusive = false;
            Drain();
        }
    }

    private void Drain()
    {
        while (_waiters.First is { Value: var waiter } && CanReserve(waiter.Bytes, waiter.UnknownSize))
        {
            _waiters.RemoveFirst();
            waiter.Node = null;
            waiter.Completion.SetResult(Reserve(waiter.Bytes, waiter.UnknownSize));
        }
    }

    internal sealed class Lease : IDisposable
    {
        private RawInputByteBudget? _owner;
        private readonly long _bytes;

        internal Lease(RawInputByteBudget owner, long bytes)
        {
            _owner = owner;
            _bytes = bytes;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_bytes);
    }

    private sealed class Waiter(RawInputByteBudget owner, long bytes, bool unknownSize, CancellationToken token)
    {
        public readonly RawInputByteBudget Owner = owner;
        public readonly long Bytes = bytes;
        public readonly bool UnknownSize = unknownSize;
        public readonly CancellationToken Token = token;
        public readonly TaskCompletionSource<Lease> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
        public CancellationTokenRegistration Registration;

        public async ValueTask<Lease> WaitAsync()
        {
            try { return await Completion.Task.ConfigureAwait(false); }
            finally { Registration.Dispose(); }
        }
    }
}
