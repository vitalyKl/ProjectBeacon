namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Json;

internal interface IDaemonLlamaSync
{
    Task SyncAsync(WorkstationSettings settings, CancellationToken ct);
    Task RunLockedAsync(Func<Task> action, CancellationToken ct);
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
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DaemonLlamaSync(DaemonDependencies deps, IDaemonRuntimeState state)
    {
        _http = deps.Http;
        _llama = deps.Llama;
        _loadSettings = deps.LoadSettings;
        _state = state;
    }

    public Task SyncAsync(WorkstationSettings settings, CancellationToken ct) =>
        RunLockedAsync(() => SyncCoreAsync(settings, ct), ct);

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

    public async Task RunLockedAsync(Func<Task> action, CancellationToken ct)
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

    public Task ReloadAsync(CancellationToken ct) =>
        RunLockedAsync(() => _llama.ReloadAsync(ct), ct);

    public Task UnloadAsync(CancellationToken ct) =>
        RunLockedAsync(() => _llama.UnloadAsync(ct), ct);

    public Task SyncNowAsync(CancellationToken ct) => SyncAsync(_loadSettings(), ct);

    public void Release() => _lock.Dispose();
}
