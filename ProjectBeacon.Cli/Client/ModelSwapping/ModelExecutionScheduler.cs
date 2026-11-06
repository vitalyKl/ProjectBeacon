namespace ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Schedules local model generations according to the LlamaSwap residency flag.
/// Non-concurrent models share one FIFO swap-group slot. Concurrent resident models
/// bypass that slot but still share the lifecycle read gate. Model-management operations
/// take the lifecycle write gate and therefore cannot restart a server during generation.
/// </summary>
internal sealed class ModelExecutionScheduler
{
    private readonly AsyncReaderWriterGate _lifecycle = new();
    private readonly AsyncReaderWriterGate _swapGroup = new();

    public Task RunModelRequestAsync(
        bool concurrent,
        Func<Task> action,
        CancellationToken ct)
        => RunModelRequestAsync(() => concurrent, action, ct);

    public async Task RunModelRequestAsync(
        Func<bool> isConcurrent,
        Func<Task> action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(isConcurrent);
        ArgumentNullException.ThrowIfNull(action);

        // Read residency only while maintenance is excluded. If the model is exclusive,
        // release the lifecycle read lease before waiting for the FIFO swap slot, then
        // reacquire it and re-check: a config update may have changed the residency flag
        // while this request was queued.
        using var initialLifecycleLease = await _lifecycle.AcquireReadAsync(ct);
        if (isConcurrent())
        {
            await action();
            return;
        }

        initialLifecycleLease.Dispose();
        using var swapLease = await _swapGroup.AcquireWriteAsync(ct);
        using var lifecycleLease = await _lifecycle.AcquireReadAsync(ct);
        if (isConcurrent())
            swapLease.Dispose();

        await action();
    }

    public async Task RunMaintenanceAsync(Func<Task> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var lease = await _lifecycle.AcquireWriteAsync(ct);
        await action();
    }

    public async Task<bool> TryRunMaintenanceAsync(Func<Task> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        ct.ThrowIfCancellationRequested();
        using var lease = _lifecycle.TryAcquireWrite();
        if (lease is null)
            return false;
        await action();
        return true;
    }
}

/// <summary>
/// FIFO asynchronous reader/writer gate. Consecutive readers may proceed together; a queued
/// writer prevents later readers from bypassing it. This allows resident model calls to overlap
/// while making model reload/configuration wait for all active requests to leave.
/// </summary>
internal sealed class AsyncReaderWriterGate
{
    private readonly object _sync = new();
    private readonly Queue<Waiter> _waiters = new();
    private int _activeReaders;
    private bool _writerActive;

    public ValueTask<IDisposable> AcquireReadAsync(CancellationToken ct = default)
        => AcquireAsync(writer: false, ct);

    public ValueTask<IDisposable> AcquireWriteAsync(CancellationToken ct = default)
        => AcquireAsync(writer: true, ct);

    public IDisposable? TryAcquireWrite()
    {
        lock (_sync)
        {
            if (_writerActive || _activeReaders != 0 || _waiters.Count != 0)
                return null;
            _writerActive = true;
            return new Lease(this, writer: true);
        }
    }

    private ValueTask<IDisposable> AcquireAsync(bool writer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Waiter? waiter;
        lock (_sync)
        {
            if (!_writerActive && _waiters.Count == 0 && (!writer || _activeReaders == 0))
            {
                if (writer)
                    _writerActive = true;
                else
                    _activeReaders++;
                return ValueTask.FromResult<IDisposable>(new Lease(this, writer));
            }

            waiter = new Waiter(this, writer, ct);
            _waiters.Enqueue(waiter);
        }

        if (ct.CanBeCanceled)
            waiter.Registration = ct.Register(static state =>
            {
                var pending = (Waiter)state!;
                pending.Owner.Cancel(pending);
            }, waiter);

        return AwaitWaiterAsync(waiter);
    }

    private static async ValueTask<IDisposable> AwaitWaiterAsync(Waiter waiter)
    {
        try
        {
            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            waiter.Registration.Dispose();
        }
    }

    private void Cancel(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Granted || waiter.Cancelled)
                return;

            waiter.Cancelled = true;
            waiter.Completion.TrySetCanceled(waiter.Token);
            PumpLocked();
        }
    }

    private void Release(bool writer)
    {
        lock (_sync)
        {
            if (writer)
            {
                if (!_writerActive)
                    throw new SynchronizationLockException("The writer lease is not active.");
                _writerActive = false;
            }
            else
            {
                if (_activeReaders <= 0)
                    throw new SynchronizationLockException("No reader lease is active.");
                _activeReaders--;
            }

            PumpLocked();
        }
    }

    private void PumpLocked()
    {
        if (_writerActive)
            return;

        while (_waiters.Count > 0 && _waiters.Peek().Cancelled)
            _waiters.Dequeue();

        if (_waiters.Count == 0)
            return;

        if (_activeReaders > 0 && _waiters.Peek().Writer)
            return;

        if (_waiters.Peek().Writer)
        {
            if (_activeReaders != 0)
                return;

            var writer = _waiters.Dequeue();
            writer.Granted = true;
            _writerActive = true;
            writer.Completion.TrySetResult(new Lease(this, writer: true));
            return;
        }

        while (_waiters.Count > 0 && !_waiters.Peek().Writer)
        {
            var reader = _waiters.Dequeue();
            if (reader.Cancelled)
                continue;

            reader.Granted = true;
            _activeReaders++;
            reader.Completion.TrySetResult(new Lease(this, writer: false));
        }
    }

    private sealed class Waiter(
        AsyncReaderWriterGate owner,
        bool writer,
        CancellationToken token)
    {
        public AsyncReaderWriterGate Owner { get; } = owner;
        public bool Writer { get; } = writer;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<IDisposable> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
        public bool Granted { get; set; }
        public bool Cancelled { get; set; }
    }

    private sealed class Lease(AsyncReaderWriterGate owner, bool writer) : IDisposable
    {
        private AsyncReaderWriterGate? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Release(writer);
    }
}
