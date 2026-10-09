namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Headers;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
/// <summary>
/// Long-polls device commands and heartbeats the control plane. Dispatches only WorkstationCommandKind values. An unknown kind throws.
/// </summary>
public sealed class WorkstationDaemon : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ClientLlamaSwap _llama;
    private readonly ClientOpenCodeServe _openCode;
    private readonly bool _ownsOpenCode;
    private readonly DaemonTiming _timing;
    private readonly IDaemonRuntimeState _state;
    private readonly IDaemonOpenCodeGate _gate;
    private readonly IDaemonLlamaSync _llamaSync;
    private readonly IDaemonCommandExecutor _executor;
    private readonly IReadOnlyList<IDaemonLoop> _loops;
    private readonly ServiceProvider _services;

    public WorkstationDaemon(
        HttpClient http,
        ClientLlamaSwap llama,
        Func<WorkstationSettings>? loadSettings = null,
        Action<string>? log = null,
        ClientOpenCodeServe? openCode = null)
    {
        _http = http;
        _llama = llama;
        _ownsOpenCode = openCode is null;
        _openCode = openCode ?? new ClientOpenCodeServe();
        _timing = new DaemonTiming();
        _state = new DaemonRuntimeState(http.BaseAddress?.ToString().TrimEnd('/') ?? "", log);
        _llama.Log = message => _state.Log(message);
        var deps = new DaemonDependencies(
            http,
            llama,
            _openCode,
            new OpenCodeAgentRuntime(_openCode),
            loadSettings ?? (() => WorkstationSettings.Load()));
        _services = DaemonServices.Build(deps, _timing, _state);
        _gate = _services.GetRequiredService<IDaemonOpenCodeGate>();
        _llamaSync = _services.GetRequiredService<IDaemonLlamaSync>();
        _executor = _services.GetRequiredService<IDaemonCommandExecutor>();
        _loops = _services.GetServices<IDaemonLoop>().ToArray();
    }

    internal TimeSpan HeartbeatInterval
    {
        get => _timing.HeartbeatInterval;
        set => _timing.HeartbeatInterval = value;
    }

    internal TimeSpan CommandErrorDelay
    {
        get => _timing.CommandErrorDelay;
        set => _timing.CommandErrorDelay = value;
    }

    internal TimeSpan ErrorBackoffInterval
    {
        get => _timing.ErrorBackoffInterval;
        set => _timing.ErrorBackoffInterval = value;
    }

    internal Func<TimeSpan, CancellationToken, Task> DelayAsync
    {
        get => _timing.DelayAsync;
        set => _timing.DelayAsync = value;
    }

    internal TimeSpan ChatPollInterval
    {
        get => _timing.ChatPollInterval;
        set => _timing.ChatPollInterval = value;
    }

    internal TimeSpan ChatIdleTimeout
    {
        get => _timing.ChatIdleTimeout;
        set => _timing.ChatIdleTimeout = value;
    }

    internal TimeSpan ChatMaxDuration
    {
        get => _timing.ChatMaxDuration;
        set => _timing.ChatMaxDuration = value;
    }

    public DaemonStatus Snapshot => _state.Snapshot;

    public Task RunAsync(CancellationToken ct)
    {
        _state.Log($"connected to {_state.Snapshot.Url}");
        return Task.WhenAll(_loops.Select(loop => loop.RunAsync(ct)));
    }

    public Task ReloadLlamaAsync(CancellationToken ct) => _llamaSync.ReloadAsync(ct);

    public Task UnloadLlamaAsync(CancellationToken ct) => _llamaSync.UnloadAsync(ct);

    public Task SyncLlamaNowAsync(CancellationToken ct) => _llamaSync.SyncNowAsync(ct);

    public void SetControlPlane(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        _http.BaseAddress = new Uri(trimmed + "/");
        _state.Publish(s => s with { Url = trimmed, Connected = false, Error = null });
        _state.Log($"control plane {trimmed}");
    }

    public void SetToken(string token)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _state.Publish(s => s with { Connected = false, Error = null });
        _state.Log("device token updated");
    }

    internal Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(CommandWire command, CancellationToken ct) =>
        _executor.ExecuteAsync(command, ct);

    public async ValueTask DisposeAsync()
    {
        await _llama.DisposeAsync();
        _llamaSync.Release();
        _gate.Release();
        await _services.DisposeAsync();
        if (_ownsOpenCode)
            await _openCode.DisposeAsync();
    }

    /// <summary>
    /// JSON shape of one claimed device command: id, kind, payload, and the project local root.
    /// </summary>
    public sealed class CommandWire
    {
        public Guid Id { get; set; }
        public WorkstationCommandKind Kind { get; set; }
        public int Version { get; set; }
        public string? PayloadJson { get; set; }
        public string? LocalRoot { get; set; }
    }

    /// <summary>
    /// YAML and port from GET /v1/devices/me/llamaswap-config.
    /// </summary>
    public sealed class LlamaSwapConfigWire
    {
        public string? Yaml { get; set; }
        public int Port { get; set; }
    }
}
