namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Enums;
using Infrastructure.LlamaSwap;

public sealed class WorkstationDaemon : IDisposable
{
    private readonly HttpClient _http;
    private readonly ClientLlamaSwap _llama;
    private readonly Func<WorkstationSettings> _loadSettings;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _llamaLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Queue<string> _logLines = new();
    private DaemonStatus _status = new();

    public WorkstationDaemon(
        HttpClient http,
        ClientLlamaSwap llama,
        Func<WorkstationSettings>? loadSettings = null,
        Action<string>? log = null)
    {
        _http = http;
        _llama = llama;
        _loadSettings = loadSettings ?? (() => WorkstationSettings.Load());
        _log = log;
        _status = new DaemonStatus { Url = http.BaseAddress?.ToString().TrimEnd('/') ?? "" };
    }

    internal TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan CommandErrorDelay { get; set; } = TimeSpan.FromSeconds(2);
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    public DaemonStatus Snapshot
    {
        get { lock (_gate) return _status; }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log($"connected to {_status.Url}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = _loadSettings();
                await WithLlamaAsync(() => SyncLlamaAsync(settings, ct), ct);
                var host = HostLoadSampler.Sample();
                var heartbeat = await _http.PostAsJsonAsync("/v1/devices/me/heartbeat", new
                {
                    probeJson = WorkstationActions.ProbeJson(_llama.StatusWire(), HostLoadSampler.ToWire(host)),
                    workstationJson = JsonSerializer.Serialize(settings, Camel)
                }, ct);
                var code = (int)heartbeat.StatusCode;
                Publish(s => s with
                {
                    Connected = heartbeat.IsSuccessStatusCode,
                    HeartbeatStatus = code,
                    LastHeartbeatAt = DateTimeOffset.Now,
                    Llama = _llama.Status,
                    Host = host,
                    Error = heartbeat.IsSuccessStatusCode ? null : $"heartbeat {code}"
                });
                if (!heartbeat.IsSuccessStatusCode)
                {
                    Log($"heartbeat {code}");
                    await DelayAsync(ErrorDelay, ct);
                    continue;
                }

                var claimed = await _http.GetAsync("/v1/devices/me/commands?wait=25", ct);
                if (claimed.StatusCode == System.Net.HttpStatusCode.NoContent)
                    continue;
                if (!claimed.IsSuccessStatusCode)
                {
                    await DelayAsync(CommandErrorDelay, ct);
                    continue;
                }

                var command = await claimed.Content.ReadFromJsonAsync<CommandWire>(Json, ct);
                if (command is null)
                    continue;
                var (ok, result, error) = await ExecuteAsync(command, ct);
                await _http.PostAsJsonAsync($"/v1/commands/{command.Id}/complete", new
                {
                    success = ok,
                    resultJson = result,
                    error
                }, ct);
                Publish(s => s with
                {
                    LastCommand = command.Kind.ToString(),
                    LastCommandAt = DateTimeOffset.Now,
                    LastCommandOk = ok,
                    Error = ok ? null : error
                });
                Log($"{command.Kind} {(ok ? "ok" : error)}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log(ex.Message);
                Publish(s => s with { Connected = false, Error = ex.Message });
                try { await DelayAsync(TimeSpan.FromSeconds(3), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public Task ReloadLlamaAsync(CancellationToken ct) =>
        WithLlamaAsync(() => _llama.ReloadAsync(ct), ct);

    public Task UnloadLlamaAsync(CancellationToken ct) =>
        WithLlamaAsync(() => _llama.UnloadAsync(ct), ct);

    public Task SyncLlamaNowAsync(CancellationToken ct) =>
        WithLlamaAsync(() => SyncLlamaAsync(_loadSettings(), ct), ct);

    public void SetControlPlane(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        _http.BaseAddress = new Uri(trimmed + "/");
        Publish(s => s with { Url = trimmed, Connected = false, Error = null });
        Log($"control plane {trimmed}");
    }

    private async Task SyncLlamaAsync(WorkstationSettings settings, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync("/v1/devices/me/llamaswap-config", ct);
            if (!response.IsSuccessStatusCode)
                return;
            var config = await response.Content.ReadFromJsonAsync<LlamaSwapConfigWire>(Json, ct);
            if (config is null)
                return;
            var port = settings.LlamaSwapPort > 0 ? settings.LlamaSwapPort : config.Port;
            await _llama.TickAsync(config.Yaml ?? "models: {}\n", port, settings.LlamaSwapBin, ct);
            Publish(s => s with { Llama = _llama.Status });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"llama-swap sync: {ex.Message}");
        }
    }

    internal async Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(CommandWire command, CancellationToken ct)
    {
        try
        {
            var kind = command.Kind;
            var payload = command.PayloadJson ?? "{}";
            if (kind == WorkstationCommandKind.ReloadProxy)
            {
                await WithLlamaAsync(() => _llama.ReloadAsync(ct), ct);
                return (true, JsonSerializer.Serialize(_llama.StatusWire()), null);
            }
            if (kind == WorkstationCommandKind.UnloadProxy)
            {
                await WithLlamaAsync(() => _llama.UnloadAsync(ct), ct);
                return (true, JsonSerializer.Serialize(_llama.StatusWire()), null);
            }
            string result = kind switch
            {
                WorkstationCommandKind.Probe => WorkstationActions.ProbeJson(_llama.StatusWire()),
                WorkstationCommandKind.ListDir => WorkstationActions.ListDir(ReadPath(payload)),
                WorkstationCommandKind.ScanGguf => WorkstationActions.ScanGguf(ReadPath(payload) ?? _loadSettings().ModelsRoot),
                WorkstationCommandKind.InitProject => WorkstationActions.InitProject(payload),
                WorkstationCommandKind.ApplyOpencode => WorkstationActions.ApplyOpencode(payload),
                WorkstationCommandKind.SaveWorkstation => WorkstationActions.SaveWorkstation(payload),
                WorkstationCommandKind.Install => WorkstationActions.Install(payload),
                _ => throw new InvalidOperationException($"Unknown command {kind}.")
            };
            return (true, result, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private async Task WithLlamaAsync(Func<Task> action, CancellationToken ct)
    {
        await _llamaLock.WaitAsync(ct);
        try { await action(); }
        finally { _llamaLock.Release(); }
    }

    private static string? ReadPath(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Log(string message)
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

    private void Publish(Func<DaemonStatus, DaemonStatus> update)
    {
        lock (_gate)
        {
            _status = update(_status);
        }
    }

    public void Dispose() => _llamaLock.Dispose();

    public sealed class CommandWire
    {
        public Guid Id { get; set; }
        public WorkstationCommandKind Kind { get; set; }
        public string? PayloadJson { get; set; }
    }

    public sealed class LlamaSwapConfigWire
    {
        public string? Yaml { get; set; }
        public int Port { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions Camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

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
