namespace ProjectBeacon.Cli.Client;

using Application.Runtime;

internal interface IDaemonOpenCodeGate
{
    ClientOpenCodeServe OpenCode { get; }
    IAgentRuntime Runtime { get; }
    Task TryTickAsync(string? projectsRoot, CancellationToken ct);
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

    public DaemonOpenCodeGate(DaemonDependencies deps)
    {
        OpenCode = deps.OpenCode;
        Runtime = deps.Runtime;
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
            await OpenCode.TickAsync(projectsRoot, ct);
        }
        finally
        {
            _lock.Release();
        }
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
