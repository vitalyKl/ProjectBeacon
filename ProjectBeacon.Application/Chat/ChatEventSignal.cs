namespace ProjectBeacon.Application.Chat;

/// <summary>
/// In-process notifications that a chat session changed (a part was appended or the status changed).
/// In self-hosted deployments the writers (chat handlers) and the readers (Blazor circuits, the SSE
/// endpoint) share the Web process, so a process-local signal is enough. Signals are edges: a signal
/// is only observed by a wait that is active when it fires; use <see cref="GetVersion"/> to detect
/// earlier changes. Session entries are created only by <see cref="Signal"/> and removed by
/// <see cref="Forget"/>, so waiters and archived sessions cannot accumulate state.
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
    // Process-wide edge: any Signal wakes every active wait, including waits on sessions that
    // have no entry yet. Callers re-check the version of the session they care about.
    private TaskCompletionSource _edge = NewWaiter();

    private static TaskCompletionSource NewWaiter() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Records a session change: bumps the version and wakes active waiters.</summary>
    public void Signal(Guid sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry))
            {
                entry = new Entry();
                _sessions[sessionId] = entry;
            }
            entry.Version++;
            entry.Waiter.TrySetResult();
            entry.Waiter = NewWaiter();
            _edge.TrySetResult();
            _edge = NewWaiter();
        }
    }

    /// <summary>Number of signals recorded for the session (0 when none or forgotten).</summary>
    public int GetVersion(Guid sessionId)
    {
        lock (_gate)
            return _sessions.TryGetValue(sessionId, out var entry) ? entry.Version : 0;
    }

    /// <summary>Removes the session's state entirely. Call after the session is archived.</summary>
    public void Forget(Guid sessionId)
    {
        lock (_gate)
            _sessions.Remove(sessionId);
    }

    /// <summary>How many sessions currently have state. Test seam.</summary>
    internal int ActiveSessions
    {
        get { lock (_gate) return _sessions.Count; }
    }
    /// <summary>
    /// Waits for a change to the session. Returns <c>true</c> when a signal fires while the wait is
    /// active (any session's signal wakes the wait; this session's version decides whether it
    /// actually changed), <c>false</c> when <paramref name="timeout"/> elapses without a change.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// Waits never create session state, so readers alone cannot leak memory.
    /// </summary>
    public async Task<bool> WaitAsync(Guid sessionId, TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        Task waiterTask;
        int versionBefore;
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var entry))
            {
                waiterTask = entry.Waiter.Task;
                versionBefore = entry.Version;
            }
            else
            {
                waiterTask = _edge.Task;
                versionBefore = 0;
            }
        }
        var finished = await Task.WhenAny(waiterTask, Task.Delay(Timeout.Infinite, cts.Token));
        if (finished == waiterTask)
            return true;
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        lock (_gate)
        {
            var versionNow = _sessions.TryGetValue(sessionId, out var entry) ? entry.Version : 0;
            // A signal can race the timeout expiry: if the version moved, report the change.
            return versionNow != versionBefore;
        }
    }
}