namespace ProjectBeacon.Cli.Client;

using Application.Runtime;

internal interface IDaemonAgentRuntimeGate
{
    IAgentRuntime Runtime { get; }
    Task TryTickAsync(string? workingDirectory, CancellationToken ct);
    Task<AgentRuntimeReadiness> EnsureReadyAsync(string? workingDirectory, CancellationToken ct);
    object RuntimeStatusWire();
    string? RuntimeWorkingDirectory { get; }
    Task RunExclusiveAsync(Func<Task> action, CancellationToken ct);
    Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct);
    void Release();
}

/// <summary>
/// Coordinates readiness and the concurrency guarantees exposed by the selected workstation runtime.
/// Native sessions own independent state; no external server process or shared runtime switch is involved.
/// </summary>
internal sealed class DaemonAgentRuntimeGate : IDaemonAgentRuntimeGate
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AgentRuntimeReadiness? _lastReadiness;

    public DaemonAgentRuntimeGate(IAgentRuntime runtime) => Runtime = runtime;

    public IAgentRuntime Runtime { get; }

    public async Task TryTickAsync(string? workingDirectory, CancellationToken ct)
    {
        if (!await _lock.WaitAsync(0, ct))
            return;
        try
        {
            await EnsureReadyAsync(workingDirectory, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AgentRuntimeReadiness> EnsureReadyAsync(string? workingDirectory, CancellationToken ct)
    {
        var readiness = Runtime is IAgentRuntimeCapabilities capabilities
            ? await capabilities.EnsureReadyAsync(workingDirectory, ct)
            : new AgentRuntimeReadiness(false, "The selected agent runtime does not expose readiness.", workingDirectory);
        _lastReadiness = readiness;
        return readiness;
    }

    public string? RuntimeWorkingDirectory => _lastReadiness?.WorkingDirectory;

    public object RuntimeStatusWire() => new
    {
        runtime = "native",
        available = true,
        healthy = _lastReadiness?.Ready ?? true,
        version = "native",
        error = _lastReadiness?.Error,
        warning = _lastReadiness?.Warning,
        cwd = _lastReadiness?.WorkingDirectory
    };

    public async Task RunExclusiveAsync(Func<Task> action, CancellationToken ct)
    {
        if (Runtime is IAgentRuntimeCapabilities { SupportsConcurrentSessions: true })
        {
            await action();
            return;
        }

        await _lock.WaitAsync(ct);
        try { await action(); }
        finally { _lock.Release(); }
    }

    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (Runtime is IAgentRuntimeCapabilities { SupportsConcurrentSessions: true })
            return await action();

        await _lock.WaitAsync(ct);
        try { return await action(); }
        finally { _lock.Release(); }
    }

    public void Release() => _lock.Dispose();
}
