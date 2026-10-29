namespace ProjectBeacon.Cli.Client;

using Application.Runtime;

internal interface IDaemonOpenCodeGate
{
    ClientOpenCodeServe OpenCode { get; }
    IAgentRuntime Runtime { get; }
    Task TryTickAsync(string? projectsRoot, CancellationToken ct);
    async Task<AgentRuntimeReadiness> EnsureReadyAsync(string? workingDirectory, CancellationToken ct)
    {
        await OpenCode.TickAsync(workingDirectory, ct);
        return new AgentRuntimeReadiness(OpenCode.Status.Healthy, OpenCode.Status.Error, OpenCode.Cwd);
    }
    object RuntimeStatusWire();
    string? RuntimeWorkingDirectory { get; }
    Task RunExclusiveAsync(Func<Task> action, CancellationToken ct);
    Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct);
    void Release();
}

/// <summary>
/// One OpenCode process and one agent runtime. Heartbeat try-locks; chat and configure wait.
/// </summary>
internal sealed class DaemonOpenCodeGate : IDaemonOpenCodeGate
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AgentRuntimeReadiness? _lastReadiness;

    public DaemonOpenCodeGate(DaemonDependencies deps) : this(deps, deps.Runtime) { }

    public DaemonOpenCodeGate(DaemonDependencies deps, IAgentRuntime runtime)
    {
        OpenCode = deps.OpenCode;
        Runtime = runtime;
    }

    public ClientOpenCodeServe OpenCode { get; }
    public IAgentRuntime Runtime { get; }

    public async Task TryTickAsync(string? projectsRoot, CancellationToken ct)
    {
        // Wait 0: a chat turn already holds this lock. Blocking here deadlocks with the llama lock.
        if (!await _lock.WaitAsync(0, ct))
            return;
        try
        {
            await EnsureReadyAsync(projectsRoot, ct);
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
            : await EnsureOpenCodeReadyAsync(workingDirectory, ct);
        _lastReadiness = readiness;
        return readiness;
    }

    public string? RuntimeWorkingDirectory => _lastReadiness?.WorkingDirectory ?? OpenCode.Cwd;

    public object RuntimeStatusWire() => Runtime is OpenCodeAgentRuntime
        ? OpenCode.StatusWire()
        : new
        {
            available = true,
            healthy = _lastReadiness?.Ready ?? true,
            version = "native",
            error = _lastReadiness?.Error,
            cwd = _lastReadiness?.WorkingDirectory
        };

    private async Task<AgentRuntimeReadiness> EnsureOpenCodeReadyAsync(string? workingDirectory, CancellationToken ct)
    {
        await OpenCode.TickAsync(workingDirectory, ct);
        return new AgentRuntimeReadiness(OpenCode.Status.Healthy, OpenCode.Status.Error, OpenCode.Cwd);
    }

    public async Task RunExclusiveAsync(Func<Task> action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Release() => _lock.Dispose();
}
