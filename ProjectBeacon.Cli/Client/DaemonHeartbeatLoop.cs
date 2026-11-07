namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Json;
using Domain.Common;
using System.Text.Json;

internal sealed class DaemonHeartbeatLoop : IDaemonLoop
{
    private readonly HttpClient _http;
    private readonly ClientLlamaSwap _llama;
    private readonly Func<WorkstationSettings> _loadSettings;
    private readonly IDaemonLlamaSync _llamaSync;
    private readonly IDaemonOpenCodeGate _gate;
    private readonly IDaemonRuntimeState _state;
    private readonly DaemonTiming _timing;

    public DaemonHeartbeatLoop(
        DaemonDependencies deps,
        IDaemonLlamaSync llamaSync,
        IDaemonOpenCodeGate gate,
        IDaemonRuntimeState state,
        DaemonTiming timing)
    {
        _http = deps.Http;
        _llama = deps.Llama;
        _loadSettings = deps.LoadSettings;
        _llamaSync = llamaSync;
        _gate = gate;
        _state = state;
        _timing = timing;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                WorkstationSettings? settings;
                try
                {
                    settings = _loadSettings();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep heartbeats flowing so the Web UI can show the unavailable runtime.
                    // A null workstationJson leaves the last successfully reported settings intact.
                    settings = null;
                    _state.Log($"workstation settings unavailable; skipping settings sync: {ex.Message}");
                }

                if (settings is not null)
                {
                    var synced = await _llamaSync.TrySyncAsync(settings, ct);
                    if (!synced)
                        _state.Log("llama-swap sync deferred while model generations are active");
                }

                // Keep the last workspace where possible; native runtime readiness must not start OpenCode.
                await _gate.TryTickAsync(_gate.RuntimeWorkingDirectory ?? settings?.ProjectsRoot, ct);
                var host = HostLoadSampler.Sample();
                var heartbeat = await _http.PostAsJsonAsync(ApiRoutes.DeviceHeartbeat, new WorkstationHeartbeatRequest(
                    WorkstationProbe.ProbeJson(
                        _llama.StatusWire(), HostLoadSampler.ToWire(host), _gate.RuntimeStatusWire()),
                    settings is null ? null : JsonSerializer.Serialize(settings, DaemonJson.Camel),
                    DaemonInstance.Id), ct);
                var code = (int)heartbeat.StatusCode;
                _state.Publish(s => s with
                {
                    Connected = heartbeat.IsSuccessStatusCode,
                    HeartbeatStatus = code,
                    LastHeartbeatAt = _timing.TimeProvider.GetLocalNow(),
                    Llama = _llama.Status,
                    Host = host,
                    Error = heartbeat.IsSuccessStatusCode ? null : $"heartbeat {code}"
                });
                if (!heartbeat.IsSuccessStatusCode)
                    _state.Log($"heartbeat {code}");
                await _timing.DelayAsync(_timing.HeartbeatInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _state.Log(ex.Message);
                _state.Publish(s => s with { Connected = false, Error = ex.Message });
                try { await _timing.DelayAsync(_timing.ErrorBackoffInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
