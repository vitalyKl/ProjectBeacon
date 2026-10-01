namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.Common;
using Application.Devices;
using Domain.Enums;
using Infrastructure.LlamaSwap;

public sealed class WorkstationDaemon : IAsyncDisposable
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
        _llama.Log = msg => Log(msg);
        _ownsOpenCode = openCode is null;
        _openCode = openCode ?? new ClientOpenCodeServe();
        _loadSettings = loadSettings ?? (() => WorkstationSettings.Load());
        _log = log;
        _status = new DaemonStatus { Url = http.BaseAddress?.ToString().TrimEnd('/') ?? "" };
    }

    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan CommandErrorDelay { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan ErrorBackoffInterval { get; set; } = TimeSpan.FromSeconds(3);
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;
    // 250ms: streams parts as they arrive without hammering the local OpenCode serve.
    internal TimeSpan ChatPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    // CPU inference routinely pauses between tokens; 10s of quiet counts the turn as done.
    internal TimeSpan ChatIdleTimeout { get; set; } = TimeSpan.FromSeconds(10);
    // Hard cap so a stuck generation cannot hold the command queue indefinitely.
    internal TimeSpan ChatMaxDuration { get; set; } = TimeSpan.FromSeconds(180);

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
                await DelayAsync(HeartbeatInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log(ex.Message);
                Publish(s => s with { Connected = false, Error = ex.Message });
                try { await DelayAsync(ErrorBackoffInterval, ct); }
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
                try { await DelayAsync(ErrorBackoffInterval, ct); }
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

    public void SetToken(string token)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Publish(s => s with { Connected = false, Error = null });
        Log("device token updated");
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
            _llama.UseOwnSwapper = settings.UseOwnSwapper;
            _llama.ConcurrentPortBase = settings.ConcurrentPortBase > 0 ? settings.ConcurrentPortBase : 9000;
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
            if (CommandSandbox.IsProjectKind(kind))
            {
                if (string.IsNullOrWhiteSpace(command.LocalRoot))
                    return (false, null, CommandSandbox.RuntimeRequired);
                if (kind == WorkstationCommandKind.ChatEnsureSession || kind == WorkstationCommandKind.ChatPrompt)
                {
                    var resolved = WorkspacePath.ResolveInRoot(command.LocalRoot, ReadPath(payload), relativeOnly: true);
                    if (!resolved.Success)
                        return (false, null, resolved.Error);
                    return kind == WorkstationCommandKind.ChatEnsureSession
                        ? await ChatEnsureAsync(resolved.Value!, payload, ct)
                        : await ChatPromptAsync(resolved.Value!, payload, ct);
                }
                if (kind == WorkstationCommandKind.RunEvalTurn)
                {
                    var evalAction = await WorkstationActions.RunEvalTurnAsync(command.LocalRoot, payload, _openCode, ChatPollInterval, ChatIdleTimeout, ChatMaxDuration, ct);
                    if (!evalAction.Success)
                        return (false, null, evalAction.Error);
                    return (true, evalAction.Value, null);
                }
                var projectAction = kind switch
                {
                    WorkstationCommandKind.InitProject => WorkstationActions.InitProject(command.LocalRoot, payload),
                    WorkstationCommandKind.ApplyOpencode => WorkstationActions.ApplyOpencode(command.LocalRoot, payload),
                    _ => null
                };
                if (projectAction is not null)
                    return projectAction.Success ? (true, projectAction.Value, null) : (false, null, projectAction.Error);
            }
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
            if (kind == WorkstationCommandKind.SwapModel)
            {
                var name = ExtractSwapName(ReadModelName(payload));
                await WithLlamaAsync(() => _llama.EnsureSwapModelAsync(name, ct), ct);
                return (true, JsonSerializer.Serialize(_llama.StatusWire()), null);
            }
            if (kind == WorkstationCommandKind.ConfigureOpenCode)
                return await ConfigureOpenCodeAsync(ct);
            var settings = _loadSettings();
            var sandboxed = kind switch
            {
                WorkstationCommandKind.ListDir => WorkstationActions.ListDir(BrowseRoot(settings, payload), ReadPath(payload)),
                WorkstationCommandKind.ScanGguf => WorkstationActions.ScanGguf(settings.ModelsRoot ?? "", ReadPath(payload)),
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

    private async Task<(bool Ok, string? Result, string? Error)> ConfigureOpenCodeAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("/v1/devices/me/opencode-connections", ct);
        if (!response.IsSuccessStatusCode)
            return (false, null, $"opencode connections {(int)response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync(ct);
        var applied = WorkstationActions.ApplyOpenCodeConnections(body);
        _openCode.SetEnvironment(applied.Environment);
        await _openCode.RestartAsync(_loadSettings().ProjectsRoot, ct);
        return (true, JsonSerializer.Serialize(new { config = applied.ConfigPath, restarted = true, providers = applied.Environment.Count }), null);
    }

    private async Task<(bool Ok, string? Result, string? Error)> ChatEnsureAsync(string cwd, string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "Chat" : "Chat";
        await _openCode.TickAsync(cwd, ct);
        if (!_openCode.Status.Healthy)
            return (false, null, _openCode.Status.Error ?? "OpenCode is not running.");
        var id = await _openCode.CreateSessionAsync(title, ct);
        return (true, JsonSerializer.Serialize(new { sessionId = id, cwd = _openCode.Cwd }), null);
    }

    private async Task<(bool Ok, string? Result, string? Error)> ChatPromptAsync(string cwd, string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var externalId = doc.RootElement.TryGetProperty("externalSessionId", out var e) ? e.GetString() : null;
        var chatId = doc.RootElement.TryGetProperty("chatSessionId", out var c) ? c.GetGuid() : Guid.Empty;
        var text = doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : null;
        var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(text) || chatId == Guid.Empty)
            return (false, null, "chatSessionId, externalSessionId, and text are required.");
        await _openCode.TickAsync(cwd, ct);
        if (!_openCode.Status.Healthy)
            return (false, null, _openCode.Status.Error ?? "OpenCode is not running.");
        if (_llama.UseOwnSwapper && model is not null)
            await WithLlamaAsync(() => _llama.EnsureSwapModelAsync(ExtractSwapName(model), ct), ct);
        await _openCode.PromptAsync(externalId, text, model, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maxQuiet = Math.Max(1, (int)Math.Ceiling(ChatIdleTimeout / ChatPollInterval));
        var maxIterations = Math.Max(1, (int)Math.Ceiling(ChatMaxDuration / ChatPollInterval));
        var quiet = 0;
        var idle = false;
        for (var i = 0; i < maxIterations && !ct.IsCancellationRequested; i++)
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
            if (quiet >= maxQuiet && seen.Count > 0)
            {
                idle = true;
                break;
            }
            await DelayAsync(ChatPollInterval, ct);
        }
        if (idle)
            Log($"chat prompt done: {seen.Count} parts, idle");
        else if (!ct.IsCancellationRequested)
            Log($"chat prompt interrupted: max duration {ChatMaxDuration.TotalSeconds:0}s reached, {seen.Count} parts");
        await _http.PostAsJsonAsync($"/v1/chat/sessions/{chatId}/idle", new { }, ct);
        return (true, JsonSerializer.Serialize(new { sessionId = externalId, interrupted = !idle }), null);
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

    private static string? ExtractSwapName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        var i = model.IndexOf('/');
        var name = i < 0 ? model : model[(i + 1)..];
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static string? ReadModelName(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
        }
        catch
        {
            return null;
        }
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

    private static string BrowseRoot(WorkstationSettings settings, string payload)
    {
        var kind = ReadString(payload, "rootKind");
        var useModels = string.Equals(kind, "models", StringComparison.OrdinalIgnoreCase);
        return (useModels ? settings.ModelsRoot : settings.ProjectsRoot) ?? "";
    }

    private static string? ReadString(string payload, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
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

    public async ValueTask DisposeAsync()
    {
        await _llama.DisposeAsync();
        _llamaLock.Dispose();
        if (_ownsOpenCode)
            await _openCode.DisposeAsync();
    }

    public sealed class CommandWire
    {
        public Guid Id { get; set; }
        public WorkstationCommandKind Kind { get; set; }
        public string? PayloadJson { get; set; }
        public string? LocalRoot { get; set; }
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
