namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Json;
using ProjectBeacon.Cli.Client.ModelSwapping;

internal interface IDaemonLlamaSync
{
    Task SyncAsync(WorkstationSettings settings, CancellationToken ct);
    async Task<bool> TrySyncAsync(WorkstationSettings settings, CancellationToken ct)
    {
        await SyncAsync(settings, ct);
        return true;
    }
    Task RunLockedAsync(Func<Task> action, CancellationToken ct);
    Task RunModelTaskAsync(
        bool concurrent,
        Func<IModelTaskLease, Task> action,
        CancellationToken ct)
        => action(NoopModelTaskLease.Instance);
    Task RunModelRequestAsync(bool concurrent, Func<Task> action, CancellationToken ct)
        => concurrent ? action() : RunLockedAsync(action, ct);
    Task RunModelRequestAsync(Func<bool> isConcurrent, Func<Task> action, CancellationToken ct)
        => RunModelRequestAsync(isConcurrent(), action, ct);
    Task ReloadAsync(CancellationToken ct);
    Task UnloadAsync(CancellationToken ct);
    Task SyncNowAsync(CancellationToken ct);
    void Release();
}

internal sealed class DaemonLlamaSync : IDaemonLlamaSync
{
    private readonly HttpClient _http;
    private readonly ClientLlamaSwap _llama;
    private readonly Func<WorkstationSettings> _loadSettings;
    private readonly IDaemonRuntimeState _state;
    private readonly ModelExecutionScheduler _scheduler = new();

    public DaemonLlamaSync(DaemonDependencies deps, IDaemonRuntimeState state)
    {
        _http = deps.Http;
        _llama = deps.Llama;
        _loadSettings = deps.LoadSettings;
        _state = state;
    }

    public Task SyncAsync(WorkstationSettings settings, CancellationToken ct) =>
        RunLockedAsync(() => SyncCoreAsync(settings, ct), ct);

    public Task<bool> TrySyncAsync(WorkstationSettings settings, CancellationToken ct) =>
        _scheduler.TryRunMaintenanceAsync(() => SyncCoreAsync(settings, ct), ct);

    private async Task SyncCoreAsync(WorkstationSettings settings, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync("/v1/devices/me/llamaswap-config", ct);
            if (!response.IsSuccessStatusCode)
                return;
            var config = await response.Content.ReadFromJsonAsync<WorkstationDaemon.LlamaSwapConfigWire>(DaemonJson.Json, ct);
            if (config is null)
                return;
            var port = settings.LlamaSwapPort > 0 ? settings.LlamaSwapPort : config.Port;
            _llama.UseOwnSwapper = settings.UseOwnSwapper;
            _llama.ConcurrentPortBase = settings.ConcurrentPortBase > 0 ? settings.ConcurrentPortBase : 9000;
            await _llama.TickAsync(config.Yaml ?? "models: {}\n", port, settings.LlamaSwapBin, ct);
            _state.Publish(s => s with { Llama = _llama.Status });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state.Log($"llama-swap sync: {ex.Message}");
        }
    }

    public Task RunLockedAsync(Func<Task> action, CancellationToken ct)
        => _scheduler.RunMaintenanceAsync(action, ct);

    public Task RunModelTaskAsync(bool concurrent, Func<IModelTaskLease, Task> action, CancellationToken ct)
        => _scheduler.RunModelTaskAsync(concurrent, action, ct);

    public Task RunModelRequestAsync(bool concurrent, Func<Task> action, CancellationToken ct)
        => _scheduler.RunModelRequestAsync(concurrent, action, ct);
    public Task RunModelRequestAsync(Func<bool> isConcurrent, Func<Task> action, CancellationToken ct)
        => _scheduler.RunModelRequestAsync(isConcurrent, action, ct);

    public Task ReloadAsync(CancellationToken ct) =>
        RunLockedAsync(() => _llama.ReloadAsync(ct), ct);

    public Task UnloadAsync(CancellationToken ct) =>
        RunLockedAsync(() => _llama.UnloadAsync(ct), ct);

    public Task SyncNowAsync(CancellationToken ct) => SyncAsync(_loadSettings(), ct);

    public void Release() { }
}
