namespace ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Schedules complete Native agent tasks according to the LlamaSwap residency flag.
/// Exclusive local models share a daemon-wide FIFO task queue. A task yields its ticket
/// while waiting for user input and resumes at its original sequence. Individual model
/// generations also use the lifecycle and swap-group gates below; maintenance cannot restart
/// a server during generation.
/// </summary>
internal interface IModelTaskLease : IDisposable
{
    Task<T> WaitForUserAsync<T>(Task<T> pending, CancellationToken ct, Action<Action> registerResumeCallback);
}

internal sealed class NoopModelTaskLease : IModelTaskLease
{
    public static NoopModelTaskLease Instance { get; } = new();
    private NoopModelTaskLease() { }
    public Task<T> WaitForUserAsync<T>(Task<T> pending, CancellationToken ct, Action<Action> registerResumeCallback) => pending.WaitAsync(ct);
    public void Dispose() { }
}

internal sealed class ModelExecutionScheduler
{
    private readonly AsyncReaderWriterGate _lifecycle = new();
    private readonly ModelTaskQueue _tasks = new();
    private readonly AsyncReaderWriterGate _swapGroup = new();

    /// <summary>
    /// Runs a complete agent task under one queue ticket. Exclusive models share a daemon-wide
    /// FIFO task queue; resident models may run independently. A task waiting for user input is
    /// skipped by dispatch without losing its original sequence.
    /// </summary>
    public async Task RunModelTaskAsync(bool concurrent, Func<IModelTaskLease, Task> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (concurrent)
        {
            await Task.Yield();
            await action(NoopModelTaskLease.Instance);
            return;
        }

        using var taskLease = await _tasks.AcquireAsync(ct);
        // The ticket is assigned before yielding so queue order follows prompt arrival rather
        // than whichever ThreadPool work item happens to start first.
        await Task.Yield();
        await action(taskLease);
    }

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


    /// <summary>
    /// Ordered cooperative queue. Waiting tickets remain in sequence order, but only Ready
    /// tickets are eligible for dispatch. A resumed ticket therefore precedes later arrivals.
    /// </summary>
    private sealed class ModelTaskQueue
    {
        private readonly object _sync = new();
        private readonly SortedDictionary<long, Ticket> _tickets = new();
        private long _nextSequence;
        private Ticket? _active;

        public async Task<IModelTaskLease> AcquireAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Ticket ticket;
            Task signal;
            lock (_sync)
            {
                ticket = new Ticket(++_nextSequence);
                _tickets.Add(ticket.Sequence, ticket);
                PumpLocked();
                signal = ticket.ActiveSignal.Task;
            }
            try
            {
                await signal.WaitAsync(ct).ConfigureAwait(false);
                return new TaskLease(this, ticket);
            }
            catch
            {
                Complete(ticket);
                throw;
            }
        }

        private async Task<T> WaitForUserAsync<T>(
            Ticket ticket,
            Task<T> pending,
            CancellationToken ct,
            Action<Action> registerResumeCallback)
        {
            if (pending.IsCompleted)
                return await pending.ConfigureAwait(false);

            Pause(ticket);
            try
            {
                // The form publishes readiness synchronously before completing its task.
                // This preserves the ticket's place even if the current task finishes at once.
                registerResumeCallback(() => MarkReady(ticket));
                var result = await pending.WaitAsync(ct).ConfigureAwait(false);
                Task activation;
                lock (_sync)
                    activation = ticket.ActiveSignal.Task;
                await activation.WaitAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch
            {
                Complete(ticket);
                throw;
            }
        }

        private void Pause(Ticket ticket)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_active, ticket) || ticket.State != TicketState.Active)
                    throw new InvalidOperationException("Only the active task can yield for user input.");
                ticket.State = TicketState.Waiting;
                ticket.ActiveSignal = NewSignal();
                _active = null;
                PumpLocked();
            }
        }

        private void MarkReady(Ticket ticket)
        {
            lock (_sync)
            {
                if (ticket.State != TicketState.Waiting || !_tickets.ContainsKey(ticket.Sequence))
                    return;
                ticket.State = TicketState.Ready;
                PumpLocked();
            }
        }

        private void Complete(Ticket ticket)
        {
            lock (_sync)
            {
                if (ticket.State == TicketState.Finished)
                    return;
                if (ReferenceEquals(_active, ticket))
                    _active = null;
                ticket.State = TicketState.Finished;
                _tickets.Remove(ticket.Sequence);
                PumpLocked();
            }
        }

        private void PumpLocked()
        {
            if (_active is not null)
                return;
            foreach (var ticket in _tickets.Values)
            {
                if (ticket.State != TicketState.Ready)
                    continue;
                ticket.State = TicketState.Active;
                _active = ticket;
                ticket.ActiveSignal.TrySetResult(true);
                return;
            }
        }

        private static TaskCompletionSource<bool> NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private enum TicketState { Ready, Active, Waiting, Finished }

        private sealed class Ticket(long sequence)
        {
            public long Sequence { get; } = sequence;
            public TicketState State { get; set; } = TicketState.Ready;
            public TaskCompletionSource<bool> ActiveSignal { get; set; } = NewSignal();
        }

        private sealed class TaskLease(ModelTaskQueue owner, Ticket ticket) : IModelTaskLease
        {
            private ModelTaskQueue? _owner = owner;
            public Task<T> WaitForUserAsync<T>(Task<T> pending, CancellationToken ct, Action<Action> registerResumeCallback)
            {
                ArgumentNullException.ThrowIfNull(pending);
                ArgumentNullException.ThrowIfNull(registerResumeCallback);
                var current = _owner ?? throw new ObjectDisposedException(nameof(TaskLease));
                return current.WaitForUserAsync(ticket, pending, ct, registerResumeCallback);
            }
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Complete(ticket);
        }
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
