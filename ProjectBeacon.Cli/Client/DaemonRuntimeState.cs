namespace ProjectBeacon.Cli.Client;

using Infrastructure.LlamaSwap;

internal interface IDaemonRuntimeState
{
    DaemonStatus Snapshot { get; }
    void Log(string message);
    void Publish(Func<DaemonStatus, DaemonStatus> update);
}

internal sealed class DaemonRuntimeState : IDaemonRuntimeState
{
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Queue<string> _logLines = new();
    private DaemonStatus _status;

    public DaemonRuntimeState(string url, Action<string>? log)
    {
        _log = log;
        _status = new DaemonStatus { Url = url };
    }

    public DaemonStatus Snapshot
    {
        get { lock (_gate) return _status; }
    }

    public void Log(string message)
    {
        lock (_gate)
        {
            _logLines.Enqueue($"{DateTime.Now:HH:mm:ss} {message}");
            while (_logLines.Count > 40)
                _logLines.Dequeue();
            _status = _status with { Log = _logLines.ToArray() };
        }
        _log?.Invoke(message);
    }

    public void Publish(Func<DaemonStatus, DaemonStatus> update)
    {
        lock (_gate)
        {
            _status = update(_status);
        }
    }
}

/// <summary>
/// Snapshot the TUI shows: connection, last heartbeat, last command, llama-swap, and host load.
/// </summary>
public sealed record DaemonStatus
{
    public string Url { get; init; } = "";
    public bool Connected { get; init; }
    public int? HeartbeatStatus { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public string? LastCommand { get; init; }
    public DateTimeOffset? LastCommandAt { get; init; }
    public bool LastCommandOk { get; init; }
    public string? Error { get; init; }
    public LlamaSwapStatusDto? Llama { get; init; }
    public HostLoadDto? Host { get; init; }
    public IReadOnlyList<string> Log { get; init; } = [];
}
