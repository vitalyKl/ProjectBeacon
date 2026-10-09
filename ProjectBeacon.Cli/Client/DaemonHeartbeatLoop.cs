namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Json;
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
                var settings = _loadSettings();
                await _llamaSync.SyncAsync(settings, ct);
                await _gate.TryTickAsync(settings.ProjectsRoot, ct);
                var host = HostLoadSampler.Sample();
                var heartbeat = await _http.PostAsJsonAsync("/v1/devices/me/heartbeat", new
                {
                    probeJson = WorkstationProbe.ProbeJson(
                        _llama.StatusWire(), HostLoadSampler.ToWire(host), _gate.OpenCode.StatusWire()),
                    workstationJson = JsonSerializer.Serialize(settings, DaemonJson.Camel)
                }, ct);
                var code = (int)heartbeat.StatusCode;
                _state.Publish(s => s with
                {
                    Connected = heartbeat.IsSuccessStatusCode,
                    HeartbeatStatus = code,
                    LastHeartbeatAt = DateTimeOffset.Now,
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
