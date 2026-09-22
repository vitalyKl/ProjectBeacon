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
    private readonly ClientOpenCodeServe _openCode;
    private readonly bool _ownsOpenCode;
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
        Action<string>? log = null,
        ClientOpenCodeServe? openCode = null)
    {
        _http = http;
        _llama = llama;
        _ownsOpenCode = openCode is null;
        _openCode = openCode ?? new ClientOpenCodeServe();
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
        await Task.WhenAll(RunHeartbeatAsync(ct), RunCommandsAsync(ct));
    }

    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = _loadSettings();
                await WithLlamaAsync(() => SyncLlamaAsync(settings, ct), ct);
                await _openCode.TickAsync(settings.ProjectsRoot, ct);
                var host = HostLoadSampler.Sample();
                var heartbeat = await _http.PostAsJsonAsync("/v1/devices/me/heartbeat", new
                {
                    probeJson = WorkstationActions.ProbeJson(
                        _llama.StatusWire(), HostLoadSampler.ToWire(host), _openCode.StatusWire()),
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
                    Log($"heartbeat {code}");
                await DelayAsync(ErrorDelay, ct);
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

    private async Task RunCommandsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
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
            if (kind == WorkstationCommandKind.ChatEnsureSession)
                return await ChatEnsureAsync(payload, ct);
            if (kind == WorkstationCommandKind.ChatPrompt)
                return await ChatPromptAsync(payload, ct);
            if (kind == WorkstationCommandKind.ChatAbort)
                return await ChatAbortAsync(payload, ct);
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
            var root = ReadRoot(payload);
            var sandboxed = kind switch
            {
                WorkstationCommandKind.ListDir => WorkstationActions.ListDir(root, ReadPath(payload)),
                WorkstationCommandKind.ScanGguf => WorkstationActions.ScanGguf(root, ReadPath(payload)),
                WorkstationCommandKind.InitProject => WorkstationActions.InitProject(root, payload),
                WorkstationCommandKind.ApplyOpencode => WorkstationActions.ApplyOpencode(root, payload),
                _ => null
            };
            if (sandboxed is not null)
            {
                if (!sandboxed.Success)
                    return (false, null, sandboxed.Error);
                return (true, sandboxed.Value, null);
            }

            string result = kind switch
            {
                WorkstationCommandKind.Probe => WorkstationActions.ProbeJson(_llama.StatusWire()),
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

    private async Task<(bool Ok, string? Result, string? Error)> ChatEnsureAsync(string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : _loadSettings().ProjectsRoot;
        var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "Chat" : "Chat";
        await _openCode.TickAsync(path, ct);
        if (!_openCode.Status.Healthy)
            return (false, null, _openCode.Status.Error ?? "OpenCode is not running.");
        var id = await _openCode.CreateSessionAsync(title, ct);
        return (true, JsonSerializer.Serialize(new { sessionId = id, cwd = _openCode.Cwd }), null);
    }

    private async Task<(bool Ok, string? Result, string? Error)> ChatPromptAsync(string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : _openCode.Cwd;
        var externalId = doc.RootElement.TryGetProperty("externalSessionId", out var e) ? e.GetString() : null;
        var chatId = doc.RootElement.TryGetProperty("chatSessionId", out var c) ? c.GetGuid() : Guid.Empty;
        var text = doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : null;
        var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(text) || chatId == Guid.Empty)
            return (false, null, "chatSessionId, externalSessionId, and text are required.");
        await _openCode.TickAsync(path, ct);
        if (!_openCode.Status.Healthy)
            return (false, null, _openCode.Status.Error ?? "OpenCode is not running.");
        await _openCode.PromptAsync(externalId, text, model, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var quiet = 0;
        for (var i = 0; i < 240 && !ct.IsCancellationRequested; i++)
        {
            var parts = await _openCode.ListPartsAsync(externalId, ct);
            var added = 0;
            foreach (var part in parts)
            {
                var key = part.ExternalId ?? $"{part.Role}:{part.Kind}:{part.Body}";
                if (!seen.Add(key) || string.Equals(part.Role, "user", StringComparison.OrdinalIgnoreCase))
                    continue;
                added++;
                await _http.PostAsJsonAsync($"/v1/chat/sessions/{chatId}/parts", new
                {
                    role = part.Role,
                    kind = part.Kind,
                    body = part.Body,
                    externalId = part.ExternalId
                }, ct);
            }
            quiet = added > 0 ? 0 : quiet + 1;
            if (quiet >= 6 && seen.Count > 0)
                break;
            await DelayAsync(TimeSpan.FromMilliseconds(250), ct);
        }
        await _http.PostAsJsonAsync($"/v1/chat/sessions/{chatId}/idle", new { }, ct);
        return (true, JsonSerializer.Serialize(new { sessionId = externalId }), null);
    }

    private async Task<(bool Ok, string? Result, string? Error)> ChatAbortAsync(string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var externalId = doc.RootElement.TryGetProperty("externalSessionId", out var e) ? e.GetString() : null;
        if (string.IsNullOrWhiteSpace(externalId))
            return (false, null, "externalSessionId is required.");
        await _openCode.AbortAsync(externalId, ct);
        return (true, "{}", null);
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

    private static string? ReadRoot(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("root", out var r) ? r.GetString() : null;
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

    public void Dispose()
    {
        _llamaLock.Dispose();
        if (_ownsOpenCode)
            _openCode.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

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
