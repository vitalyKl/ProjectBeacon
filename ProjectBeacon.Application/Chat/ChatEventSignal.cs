namespace ProjectBeacon.Application.Chat;

/// <summary>
/// In-process notifications that a chat session changed (a part was appended or the status changed).
/// In self-hosted deployments the writer (chat handlers) and the readers (Blazor circuits, the SSE
/// endpoint) share the Web process, so a process-local signal is enough. Signals are edges: a signal
/// is only observed by a wait that is active when it fires; use <see cref="GetVersion"/> to detect
/// earlier changes. Pair with a periodic refresh safety net, since the signal is in-memory only.
/// </summary>
public sealed class ChatEventSignal
{
    private sealed class Entry
    {
        public int Version { get; set; }
        public TaskCompletionSource Waiter { get; set; } = NewWaiter();
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _sessions = new();

    private static TaskCompletionSource NewWaiter() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Records a session change. Call after a successful database write.</summary>
    public void Signal(Guid sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.ContainsKey(sessionId))
                _sessions[sessionId] = new Entry();
            var entry = _sessions[sessionId];
            entry.Version++;
            entry.Waiter.TrySetResult();
            entry.Waiter = NewWaiter();
        }
    }

    /// <summary>Number of signals recorded for the session (0 when none).</summary>
    public int GetVersion(Guid sessionId)
    {
        lock (_gate)
            return _sessions.TryGetValue(sessionId, out var entry) ? entry.Version : 0;
    }

    /// <summary>
    /// Waits for a change to the session. Returns <c>true</c> when a signal fires while the wait is
    /// active, <c>false</c> when <paramref name="timeout"/> elapses without a signal. Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<bool> WaitAsync(Guid sessionId, TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        while (true)
        {
            Task waiterTask;
            lock (_gate)
            {
                if (!_sessions.ContainsKey(sessionId))
                    _sessions[sessionId] = new Entry();
                waiterTask = _sessions[sessionId].Waiter.Task;
            }
            var finished = await Task.WhenAny(waiterTask, Task.Delay(Timeout.Infinite, cts.Token));
            if (finished == waiterTask)
                return true;
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            lock (_gate)
            {
                if (_sessions[sessionId].Waiter.Task == waiterTask)
                    return false;
                // A signal raced with the expiry; the re-wait exits immediately.
            }
        }
    }
}