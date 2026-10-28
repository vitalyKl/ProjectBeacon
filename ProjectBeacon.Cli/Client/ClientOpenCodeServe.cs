namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
/// <summary>
/// Token counts and cost reported by one OpenCode session.
/// </summary>
public sealed record OpenCodeSessionUsage(int PromptTokens, int CompletionTokens, int AssistantMessages, double TotalCost);
/// <summary>
/// Local OpenCode server used by chat and eval. Creating a session fails when OpenCode does not return an id.
/// Supports OpenCode 1.x (API at the root, no auth) and 2.x (API under /api with Basic auth); the 2.x
/// server password is read from serve stdout and kept in memory only.
/// </summary>
public sealed class ClientOpenCodeServe : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    // The event stream lives as long as the chat turn; the 30s request timeout must not cut it.
    private readonly HttpClient _streamHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private Process? _process;
    private const int MaxRestarts = 5;
    private int _unhealthyPolls;
    private int _restarts;
    private Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private string? _cwd;
    private int _port = 4096;
    private bool _runningFake;
    private volatile string? _serverPassword;
    // 0 = unknown, 1 = v1, 2 = v2; set by health polling and probed on demand for ungated calls (abort).
    private volatile int _detectedVersion;
    private DateTime _passwordCapturedAt = DateTime.MinValue;
    private DateTime _startedAt = DateTime.MinValue;
    private int _passwordFromPid;
    private static readonly Regex PasswordLinePattern = new(@"^\s*server password\s+(\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public OpenCodeServeStatus Status { get; private set; } = OpenCodeServeStatus.Missing("opencode is not started on this device.");
    public int Port => _port;
    public string? Cwd => _cwd;
    internal int StartCount { get; private set; }
    internal bool SkipRealProcess { get; set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendAsync { get; set; }
    internal Func<string, Func<string, string, int, string, CancellationToken, Task>, CancellationToken, Task>? TestStream { get; set; }
    internal string? ServerPassword => _serverPassword;
    internal bool TestForceRunning { get; set; }
    internal Func<int, int>? PortHolderProbe { get; set; }
    internal Func<string, string?>? BinaryProbe { get; set; }
    internal int TestSettleSeconds { get; set; } = 10;
    internal void TestSetStartedAt(DateTime at) => _startedAt = at;
    private int _lastBusyHolderLogged;
    internal Action<string>? Log { get; set; }

    public object StatusWire() => new
    {
        available = Status.Available,
        healthy = Status.Healthy,
        port = _port,
        cwd = _cwd,
        version = Status.Version,
        error = Status.Error
    };

    public void SetEnvironment(IReadOnlyDictionary<string, string> env) =>
        _env = new Dictionary<string, string>(env, StringComparer.Ordinal);

    public async Task RestartAsync(string? cwd, CancellationToken ct)
    {
        _restarts = 0;
        _unhealthyPolls = 0;
        await KillProcessAsync();
        await TickAsync(cwd, ct);
    }

    public async Task TickAsync(string? cwd, CancellationToken ct)
    {
        var next = string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFullPath(cwd);
        var bin = BinaryProbe is { } probe
            ? probe("opencode") ?? probe("opencode.exe")
            : WorkstationProbe.Which("opencode") ?? WorkstationProbe.Which("opencode.exe");
        if (string.IsNullOrWhiteSpace(bin) && !SkipRealProcess && !TestForceRunning)
        {
            await KillProcessAsync();
            SetStatus(OpenCodeServeStatus.Missing("opencode binary not found on this device."));
            return;
        }

        if (!string.Equals(next, _cwd, StringComparison.OrdinalIgnoreCase) || !IsRunning)
        {
            _restarts = 0;
            if (_process is not null)
                Log?.Invoke($"opencode: restarting serve (cwd changed or process stopped); cwd={next}");
            await KillProcessAsync();
        }

        _cwd = next;
        if (string.IsNullOrWhiteSpace(_cwd))
        {
            SetStatus(OpenCodeServeStatus.Missing("No project folder to start OpenCode in."));
            return;
        }

        Directory.CreateDirectory(_cwd);
        if (!EnsureProcess(bin ?? "opencode"))
            return;
        await PollAsync(ct);
        if (SkipRealProcess || !IsRunning || Status.Healthy)
        {
            _unhealthyPolls = 0;
            if (Status.Healthy)
                _restarts = 0;
            return;
        }

        if (++_unhealthyPolls < 3)
            return;
        _unhealthyPolls = 0;
        if (_restarts >= MaxRestarts)
        {
            SetStatus(new OpenCodeServeStatus(Status.Available, false, Status.Version, $"opencode restart limit ({MaxRestarts})."));
            return;
        }
        _restarts++;
        await KillProcessAsync();
        if (!EnsureProcess(bin ?? "opencode"))
            return;
        await PollAsync(ct);
    }

    public async Task<string> CreateSessionAsync(string title, CancellationToken ct)
    {
        if (SkipRealProcess)
            return "oc_test";
        using var response = await Send(HttpMethod.Post, IsV2 ? "/api/session" : "/session", new { title }, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ReadId(doc.RootElement) ?? throw new InvalidOperationException("OpenCode session id missing.");
    }

    public async Task PromptAsync(string sessionId, string text, string? model, Application.Runtime.AgentPromptControls? controls, CancellationToken ct)
    {
        if (SkipRealProcess)
            return;
        if (IsV2)
        {
            // v2 sets the model on the session and the prompt payload carries text only;
            // temperature/tools have no v2 equivalent; reasoning effort travels as the model variant.
            if (!string.IsNullOrWhiteSpace(model))
            {
                var (providerID, modelID) = SplitModelRef(model);
                // v2 exposes reasoning effort as a model variant (defined per model in the apply payload);
                // temperature and tool permissions remain v1-only.
                var effort = controls?.ReasoningEffort;
                var modelRef = new Dictionary<string, object?> { ["id"] = modelID, ["providerID"] = providerID };
                if (!string.IsNullOrWhiteSpace(effort) && !string.Equals(effort, "default", StringComparison.OrdinalIgnoreCase))
                    modelRef["variant"] = effort;
                using var switched = await Send(HttpMethod.Post, $"/api/session/{Uri.EscapeDataString(sessionId)}/model", new { model = modelRef }, ct);
                switched.EnsureSuccessStatusCode();
            }
            using var v2Response = await Send(HttpMethod.Post, $"/api/session/{Uri.EscapeDataString(sessionId)}/prompt", new { text }, ct);
            if (v2Response.StatusCode != System.Net.HttpStatusCode.NoContent)
                v2Response.EnsureSuccessStatusCode();
            return;
        }
        var body = new Dictionary<string, object?>
        {
            ["parts"] = new object[] { new { type = "text", text } }
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            var (providerID, modelID) = SplitModelRef(model);
            body["model"] = new { providerID, modelID };
        }
        if (controls?.Temperature is double temperature)
            body["temperature"] = temperature;
        if (!string.IsNullOrWhiteSpace(controls?.ReasoningEffort))
            body["reasoningEffort"] = controls.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(controls?.ToolPermissions))
            body["tools"] = controls.ToolPermissions;
        using var response = await Send(HttpMethod.Post, $"/session/{sessionId}/prompt_async", body, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
            response.EnsureSuccessStatusCode();
    }

    internal int AbortCount { get; private set; }

    public async Task AbortAsync(string sessionId, CancellationToken ct)
    {
        AbortCount++;
        if (SkipRealProcess)
            return;
        if (await IsV2Async(ct))
        {
            // A v2 interrupt needs Basic auth, but a queued abort can reach a fresh serve
            // before its password line is read from stdout; wait for it briefly.
            if (_serverPassword is null)
                await WaitForPasswordAsync(ct);
            using var v2Response = await Send(HttpMethod.Post, $"/api/session/{Uri.EscapeDataString(sessionId)}/interrupt", null, ct);
            v2Response.EnsureSuccessStatusCode();
            return;
        }
        using var response = await Send(HttpMethod.Post, $"/session/{sessionId}/abort", new { }, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Resolves the serve major version on demand so path selection never depends on the
    /// captured password: an ungated abort can race the v2 handshake at serve start.
    /// </summary>
    private async Task<bool> IsV2Async(CancellationToken ct)
    {
        if (_serverPassword is not null || _detectedVersion == 2)
            return true;
        if (_detectedVersion == 1)
            return false;
        bool v2 = false;
        try
        {
            using var probe = await Send(HttpMethod.Get, "/api/info", null, ct);
            v2 = probe.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Unauthorized;
            if (v2)
                _detectedVersion = 2;
            else if (probe.StatusCode == System.Net.HttpStatusCode.NotFound)
                _detectedVersion = 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        return v2;
    }

    private async Task WaitForPasswordAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (_serverPassword is null)
                await Task.Delay(100, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // No password within the wait window; the interrupt request surfaces the auth error.
        }
    }

    public async Task<IReadOnlyList<OpenCodeMessagePart>> ListPartsAsync(string sessionId, CancellationToken ct)
    {
        if (SkipRealProcess)
            return [new OpenCodeMessagePart("assistant", "text", "ok", "fake-1")];
        if (IsV2)
            return ParseParts(await ReadAllMessagesAscAsync(sessionId, ct), IsV2);
        using var response = await Send(HttpMethod.Get, MessagePath(sessionId), null, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseParts(doc.RootElement, IsV2);
    }

    public async Task<Application.Runtime.AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct)
    {
        if (SkipRealProcess)
            return null;
        using var response = await Send(HttpMethod.Get, TurnStatusPath(sessionId), null, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return IsV2 ? ParseLatestTurnStatus(doc.RootElement) : ParseTurnStatus(doc.RootElement);
    }
    public async Task<OpenCodeSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
    {
        if (SkipRealProcess)
            return new OpenCodeSessionUsage(12, 34, 1, 0.0001);
        if (IsV2)
            return ParseUsage(await ReadAllMessagesAscAsync(sessionId, ct));
        using var response = await Send(HttpMethod.Get, MessagePath(sessionId), null, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseUsage(doc.RootElement);
    }

    /// <summary>
    /// The context limit of the session's model from the server catalog. Falls back to the
    /// first listed model when none is selected; null when the catalog has no usable entry.
    /// </summary>
    public async Task<int?> ReadContextLimitAsync(string? modelId, CancellationToken ct)
    {
        if (SkipRealProcess)
            return null;
        using var response = await Send(HttpMethod.Get, IsV2 ? "/api/model" : "/app/models", null, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            root = data;
        if (root.ValueKind is not JsonValueKind.Array)
            return null;
        string? wantProvider = null;
        string? wantModel = null;
        if (!string.IsNullOrWhiteSpace(modelId))
            (wantProvider, wantModel) = SplitModelRef(modelId);
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind is not JsonValueKind.Object)
                continue;
            var provider = entry.TryGetProperty("providerID", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var model = entry.TryGetProperty("modelID", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (wantModel is not null && (model != wantModel || (wantProvider is not null && provider != wantProvider)))
                continue;
            if (entry.TryGetProperty("limit", out var limit)
                && limit.ValueKind == JsonValueKind.Object
                && limit.TryGetProperty("context", out var context)
                && context.ValueKind == JsonValueKind.Number
                && context.TryGetInt32(out var value))
                return value;
            if (wantModel is null)
                return null;
        }
        return null;
    }

    /// <summary>
    /// Subscribes to the v2 global event feed and reports the live text/reasoning deltas of one session.
    /// Deltas are ephemeral (the feed is volatile), so this is a display accelerator only: the chat
    /// poll loop re-reads the durable message list and remains the correctness backstop. One
    /// connection per call, no reconnect. No-op for v1 servers and for fake test processes.
    /// </summary>
    public Task StreamSessionTextAsync(
        string sessionId,
        Func<string, string, int, string, CancellationToken, Task> onDelta,
        CancellationToken ct)
    {
        if (TestStream is { } hook)
            return hook(sessionId, onDelta, ct);
        if (SkipRealProcess || !IsV2 || _serverPassword is null)
            return Task.CompletedTask;
        return StreamSessionTextLockedAsync(sessionId, onDelta, ct);
    }

    private async Task StreamSessionTextLockedAsync(
        string sessionId,
        Func<string, string, int, string, CancellationToken, Task> onDelta,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_port}/api/event");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + _serverPassword));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        HttpResponseMessage response;
        try
        {
            response = SendAsync is { } hook
                ? await hook(request, ct)
                : await _streamHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke($"opencode event stream: connect failed: {ex.Message}");
            return;
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await LogFailure(response, HttpMethod.Get, "/api/event");
                return;
            }
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                string? line;
                while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                        continue; // heartbeats (": heartbeat") and blank frame separators
                    try
                    {
                        using var doc = JsonDocument.Parse(line[5..].TrimStart());
                        var root = doc.RootElement;
                        var type = ReadString(root, "type");
                        if (type is not ("session.text.delta" or "session.reasoning.delta")
                            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                            continue;
                        var session = ReadString(data, "sessionID");
                        var messageId = ReadString(data, "assistantMessageID");
                        var delta = ReadString(data, "delta");
                        if (session is null || session != sessionId || messageId is null || messageId.Length == 0 || delta is null)
                            continue;
                        var ordinal = data.TryGetProperty("ordinal", out var ord) && ord.ValueKind == JsonValueKind.Number
                            ? ord.GetInt32()
                            : 0;
                        await onDelta(type == "session.text.delta" ? "text" : "reasoning", messageId, ordinal, delta, ct);
                    }
                    catch (JsonException)
                    {
                        // Skip malformed frames.
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The turn ended; drop the stream on purpose.
            }
            catch (Exception ex)
            {
                Log?.Invoke($"opencode event stream: closed: {ex.Message}");
            }
        }
    }

    private string MessagePath(string sessionId) =>
        IsV2 ? $"/api/session/{Uri.EscapeDataString(sessionId)}/message" : $"/session/{sessionId}/message";

    // The v2 server lists newest-first by default; only the tail of the session decides whether a turn ended.
    private string TurnStatusPath(string sessionId) =>
        IsV2 ? $"/api/session/{Uri.EscapeDataString(sessionId)}/message?order=desc&limit=20" : MessagePath(sessionId);

    // v2 pages the message list (limit 1..200, default 50); follow cursor.next until exhausted.
    private static string AscMessagesPath(string sessionId) =>
        $"/api/session/{Uri.EscapeDataString(sessionId)}/message?order=asc&limit=200";

    private static string? NextPagePath(string sessionId, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("cursor", out var cursor) || cursor.ValueKind != JsonValueKind.Object
            || !cursor.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
            return null;
        return $"/api/session/{Uri.EscapeDataString(sessionId)}/message?cursor={Uri.EscapeDataString(next.GetString()!)}";
    }

    private async Task<IReadOnlyList<string>> ReadAllMessagesAscAsync(string sessionId, CancellationToken ct)
    {
        var raw = new List<string>();
        var path = AscMessagesPath(sessionId);
        var pages = 0;
        while (path is not null && pages++ < 500)
        {
            using var response = await Send(HttpMethod.Get, path, null, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (MessageArray(doc.RootElement) is { } array)
                foreach (var message in array.EnumerateArray())
                    raw.Add(message.GetRawText());
            path = NextPagePath(sessionId, doc.RootElement);
        }
        return raw;
    }
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_port}{path}");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (_serverPassword is { } password && path.StartsWith("/api/", StringComparison.Ordinal))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        var response = SendAsync is not null
            ? await SendAsync(request, ct)
            : await _http.SendAsync(request, ct);
        await LogFailure(response, method, path);
        return response;
    }

    private bool IsRunning => TestForceRunning || (SkipRealProcess ? _runningFake : _process is { HasExited: false });

    private bool IsV2 => _serverPassword is not null || _detectedVersion == 2;

    private void SetStatus(OpenCodeServeStatus next)
    {
        if (Status.Available == next.Available && Status.Healthy == next.Healthy && Status.Version == next.Version && Status.Error == next.Error)
            return;
        Status = next;
        Log?.Invoke(next.Healthy ? $"opencode healthy: {next.Version ?? "unknown"}" : $"opencode unhealthy: {next.Error ?? "no detail"}");
    }

    private async Task LogFailure(HttpResponseMessage response, HttpMethod method, string path)
    {
        if ((int)response.StatusCode < 400)
            return;
        string detail = "";
        try
        {
            detail = Clip(await response.Content.ReadAsStringAsync(), 300);
        }
        catch
        {
        }
        Log?.Invoke($"opencode http: {method.Method} {path} -> {(int)response.StatusCode} {detail}".TrimEnd());
    }

    private static string Clip(string value, int max = 200)
    {
        var single = value.Replace("\r", " ").Replace("\n", " ");
        return single.Length <= max ? single : single[..max] + "...";
    }

    private static int SecondsSince(DateTime at) =>
        at == DateTime.MinValue ? 0 : Math.Max(0, (int)(DateTime.UtcNow - at).TotalSeconds);

    private static (string ProviderID, string ModelID) SplitModelRef(string model)
    {
        var i = model.IndexOf('/', StringComparison.Ordinal);
        return i > 0 ? (model[..i], model[(i + 1)..]) : ("opencode", model);
    }

    internal void HandleStdoutLine(string line) => HandleStdoutLine(line, _process?.Id ?? 0);

    private void HandleStdoutLine(string line, int sourcePid)
    {
        var match = PasswordLinePattern.Match(line);
        if (match.Success)
        {
            var password = match.Groups[1].Value;
            _serverPassword = password;
            _passwordCapturedAt = DateTime.UtcNow;
            _passwordFromPid = sourcePid;
            Log?.Invoke($"opencode: server password captured ({password.Length} chars, from pid {_passwordFromPid})");
            return;
        }
        Log?.Invoke($"opencode stdout: {Clip(line)}");
    }

    private void HandleProcessExit(Process process)
    {
        try
        {
            Log?.Invoke($"opencode: serve pid={process.Id} exited (code {process.ExitCode})");
        }
        catch
        {
            Log?.Invoke($"opencode: serve pid={process.Id} exited");
        }
    }

    private bool EnsureProcess(string bin)
    {
        if (IsRunning)
            return true;
        if (SkipRealProcess)
        {
            _runningFake = true;
            StartCount++;
            SetStatus(OpenCodeServeStatus.Ok(null));
            return true;
        }
        var holder = PortHolderProbe is { } probe ? probe(_port) : ProcessControl.FindListeningPortPid(_port);
        if (holder > 0)
        {
            _serverPassword = null;
            _detectedVersion = 0;
            _passwordCapturedAt = DateTime.MinValue;
            _passwordFromPid = 0;
            if (_lastBusyHolderLogged != holder)
            {
                _lastBusyHolderLogged = holder;
                Log?.Invoke($"opencode: port {_port} is already in use by pid {holder}; not launching serve");
            }
            SetStatus(new OpenCodeServeStatus(true, false, null,
                $"port {_port} is in use by another process (pid {holder}); our serve cannot start. Stop that process or restart the client."));
            return false;
        }
        _lastBusyHolderLogged = 0;
        StartCount++;
        var psi = new ProcessStartInfo
        {
            FileName = bin,
            WorkingDirectory = _cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--hostname");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(_port.ToString());
        foreach (var pair in _env)
            psi.Environment[pair.Key] = pair.Value;
        _serverPassword = null;
        _detectedVersion = 0;
        _passwordCapturedAt = DateTime.MinValue;
        _passwordFromPid = 0;
        _startedAt = DateTime.UtcNow;
        int launchedPid = 0;
        var launched = ProcessRunner.TryStart(
            psi,
            line => HandleStdoutLine(line, launchedPid),
            line => Log?.Invoke($"opencode stderr: {Clip(line)}"),
            HandleProcessExit);
        launchedPid = launched.Process?.Id ?? 0;
        if (launched.Error == ProcessLaunchError.NotStarted)
        {
            SetStatus(OpenCodeServeStatus.Missing("opencode serve failed to start."));
            return true;
        }
        if (launched.Error == ProcessLaunchError.StartThrew)
        {
            SetStatus(OpenCodeServeStatus.Missing($"opencode serve failed to start: {launched.ExceptionMessage}"));
            return true;
        }
        if (launched.Error == ProcessLaunchError.Drain)
        {
            SetStatus(OpenCodeServeStatus.Missing("opencode serve failed to drain output."));
            return true;
        }
        _process = launched.Process;
        Log?.Invoke($"opencode: launched serve pid={_process!.Id} bin={bin} cwd={_cwd}");
        return true;
    }

    private async Task KillProcessAsync()
    {
        if (SkipRealProcess)
        {
            _runningFake = false;
            return;
        }
        var process = _process;
        _process = null;
        if (process is null)
            return;
        Log?.Invoke($"opencode: killing serve pid={process.Id}");
        await ProcessControl.KillAsync(process);
        try
        {
            if (process.HasExited)
                Log?.Invoke($"opencode: serve pid={process.Id} exited (code {process.ExitCode})");
            else
                Log?.Invoke($"opencode: serve pid={process.Id} did not exit within the kill timeout");
        }
        catch
        {
        }
        try { process.Dispose(); } catch { }
    }

    internal async Task PollAsync(CancellationToken ct)
    {
        if (SkipRealProcess)
        {
            SetStatus(OpenCodeServeStatus.Ok(null));
            return;
        }
        // A fresh serve needs a moment to bind and print its password; a single probe races it. Retry until healthy.
        while (true)
        {
            if (await PollOnceAsync(ct))
                return;
            if (_startedAt == DateTime.MinValue || SecondsSince(_startedAt) >= TestSettleSeconds)
                return;
            await Task.Delay(1000, ct);
        }
    }

    private async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        try
        {
            using var probe = await Send(HttpMethod.Get, "/api/info", null, ct);
            if (probe.StatusCode == System.Net.HttpStatusCode.OK)
            {
                var version = await TryReadInfoVersionAsync(probe, ct);
                if (version is not null)
                {
                    _detectedVersion = 2;
                    SetStatus(OpenCodeServeStatus.Ok(version));
                    return true;
                }
                // 200 without a JSON info body: not the v2 info endpoint; fall through to v1 health.
            }
            else if (probe.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _detectedVersion = 2;
                string message;
                if (!IsRunning)
                    message = $"opencode v2 on port {_port} but the client's own process is not running; an older opencode serve may occupy the port. Stop it and restart the client.";
                else if (_serverPassword is { } password)
                    message = $"/api/info rejected credentials at http://127.0.0.1:{_port} (captured {password.Length}-char password, {SecondsSince(_passwordCapturedAt)}s ago from pid {_passwordFromPid}; serve pid {_process?.Id ?? 0}; process up {SecondsSince(_startedAt)}s).";
                else
                    message = "opencode v2 detected; waiting for server password from serve output.";
                SetStatus(new OpenCodeServeStatus(true, false, null, message));
                return false;
            }
            else if (probe.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                SetStatus(new OpenCodeServeStatus(true, false, null, $"/api/info {(int)probe.StatusCode}"));
                return false;
            }
            // v1 servers do not mount /api; fall back to the classic health endpoint.
            using var health = await Send(HttpMethod.Get, "/global/health", null, ct);
            if (!health.IsSuccessStatusCode)
            {
                SetStatus(new OpenCodeServeStatus(true, false, null, $"/global/health {(int)health.StatusCode}"));
                return false;
            }
            var json = await health.Content.ReadAsStringAsync(ct);
            string? healthVersion = null;
            var healthy = false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    healthy = doc.RootElement.TryGetProperty("healthy", out var h) && h.ValueKind == JsonValueKind.True;
                    healthVersion = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                }
            }
            catch (JsonException)
            {
            }
            if (healthy)
                _detectedVersion = 1;
            SetStatus(healthy
                ? OpenCodeServeStatus.Ok(healthVersion)
                : new OpenCodeServeStatus(true, false, null, "/global/health did not return a JSON health body."));
            return healthy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(new OpenCodeServeStatus(true, false, null, $"opencode serve not reachable: {ex.Message}"));
            return false;
        }
    }

    private static async Task<string?> TryReadInfoVersionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty("pid", out _) && !doc.RootElement.TryGetProperty("version", out _))
                return null;
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadId(JsonElement root)
    {
        if (root.TryGetProperty("id", out var id))
            return id.GetString();
        if (root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("id", out var nested))
            return nested.GetString();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var dataId))
            return dataId.GetString();
        return null;
    }

    private static IReadOnlyList<OpenCodeMessagePart> ParseParts(JsonElement root, bool isV2)
    {
        var list = new List<OpenCodeMessagePart>();
        if (MessageArray(root) is not { } messages)
            return list;
        foreach (var message in messages.EnumerateArray())
            AddMessageParts(list, message, isV2);
        return list;
    }

    private static IReadOnlyList<OpenCodeMessagePart> ParseParts(IReadOnlyList<string> rawMessages, bool isV2)
    {
        var list = new List<OpenCodeMessagePart>();
        foreach (var raw in rawMessages)
        {
            using var doc = JsonDocument.Parse(raw);
            AddMessageParts(list, doc.RootElement, isV2);
        }
        return list;
    }

    private static void AddMessageParts(List<OpenCodeMessagePart> list, JsonElement message, bool isV2)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return;
        // v1 wraps the info as {info, parts}; v2 items are the info objects themselves, tagged by type.
        JsonElement info = default;
        if (message.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
            info = infoEl;
        else if (message.TryGetProperty("type", out _))
            info = message;
        var role = "assistant";
        if (info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
                role = roleEl.GetString() ?? role;
            else if (info.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            {
                var type = typeEl.GetString();
                if (type is not ("user" or "assistant"))
                    return;
                role = type;
            }
        }
        else if (message.TryGetProperty("role", out var topRole) && topRole.ValueKind == JsonValueKind.String)
        {
            role = topRole.GetString() ?? role;
        }
        // Assistant parts are relayed only once their message is final, so streamed text is never posted mid-stream.
        var complete = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? MessageCompleted(info.ValueKind == JsonValueKind.Object ? info : message)
            : true;
        var infoId = info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;
        JsonElement? parts = null;
        if (message.TryGetProperty("parts", out var partsEl) && partsEl.ValueKind == JsonValueKind.Array)
            parts = partsEl;
        else if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
            parts = contentEl;
        if (parts is not null)
        {
            // Fallback ids are kind-scoped ordinals: the durable content array interleaves reasoning,
            // text, and tool parts, so a plain array index does not match the live event ordinals.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var part in parts.Value.EnumerateArray())
            {
                if (infoId is null)
                {
                    AddPart(list, role, part, null, complete, isV2);
                    continue;
                }
                var rawKind = part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? "text"
                    : "text";
                var normKind = rawKind is "reasoning" or "thinking" ? "reasoning" : rawKind;
                var ordinal = counts.TryGetValue(normKind, out var n) ? n : 0;
                counts[normKind] = ordinal + 1;
                AddPart(list, role, part, $"{infoId}:{normKind}:{ordinal}", complete, isV2);
            }
            return;
        }
        if (role == "user" && info.ValueKind == JsonValueKind.Object)
        {
            var text = info.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String ? textEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text))
                list.Add(new OpenCodeMessagePart("user", "text", text!, infoId, true));
        }
    }

    private static Application.Runtime.AgentTurnStatus? ParseTurnStatus(JsonElement root)
    {
        if (MessageArray(root) is not { } messages)
            return null;
        Application.Runtime.AgentTurnStatus? last = null;
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
                continue;
            JsonElement info = default;
            if (message.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
                info = infoEl;
            else if (message.TryGetProperty("type", out _))
                info = message;
            var role = info.ValueKind == JsonValueKind.Object
                ? ReadString(info, "role") ?? ReadString(info, "type")
                : ReadString(message, "role");
            if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;
            var finish = info.ValueKind == JsonValueKind.Object ? ReadString(info, "finish") : null;
            var error = info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.Object
                ? ReadString(errorEl, "message") ?? "error"
                : null;
            // A tool-calls finish means the agent loop continues with a new message, so the turn is not done.
            var ended = error is not null
                || ((finish is null || !string.Equals(finish, "tool-calls", StringComparison.OrdinalIgnoreCase))
                && (MessageCompleted(info) || finish is not null));
            var messageId = info.ValueKind == JsonValueKind.Object ? ReadString(info, "id") : null;
            last = new Application.Runtime.AgentTurnStatus(ended, finish, error, messageId);
        }
        return last;
    }

    // v2 lists newest-first: the newest idle message closes the turn; otherwise the newest
    // assistant message decides. A failed idle keeps scanning for the assistant error.
    private static Application.Runtime.AgentTurnStatus? ParseLatestTurnStatus(JsonElement root)
    {
        if (MessageArray(root) is not { } messages)
            return null;
        string? idleId = null;
        string? idleOutcome = null;
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
                continue;
            var type = ReadString(message, "type");
            if (string.Equals(type, "idle", StringComparison.OrdinalIgnoreCase))
            {
                if (idleId is null)
                {
                    idleId = ReadString(message, "id");
                    idleOutcome = ReadString(message, "outcome");
                }
                continue;
            }
            if (string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadString(message, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
            {
                var finish = ReadString(message, "finish");
                var error = message.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.Object
                    ? ReadString(errorEl, "message") ?? "error"
                    : null;
                if (idleId is not null)
                    return new Application.Runtime.AgentTurnStatus(true, idleOutcome, error, idleId);
                var ended = error is not null
                    || ((finish is null || !string.Equals(finish, "tool-calls", StringComparison.OrdinalIgnoreCase))
                    && (MessageCompleted(message) || finish is not null));
                return new Application.Runtime.AgentTurnStatus(ended, finish, error, ReadString(message, "id"));
            }
        }
        return idleId is not null
            ? new Application.Runtime.AgentTurnStatus(true, idleOutcome, null, idleId)
            : null;
    }


    private static bool MessageCompleted(JsonElement info)
    {
        return info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object
            && time.TryGetProperty("completed", out _);
    }

    private static JsonElement? MessageArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "messages", "data" })
            {
                if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    return arr;
            }
        }
        return null;
    }

    private static void AddPart(List<OpenCodeMessagePart> list, string role, JsonElement part, string? fallbackId, bool complete, bool isV2)
    {
        var kind = part.TryGetProperty("type", out var t) ? t.GetString() ?? "text" : "text";
        var id = ReadString(part, "id") ?? fallbackId;
        if (kind is "tool" or "tool-call" or "tool_use" or "tool_call")
        {
            var name = ReadString(part, "tool") ?? ReadString(part, "name") ?? "tool";
            var status = "";
            string? toolInput = null;
            int? durationMs = null;
            var summary = "";
            if (part.TryGetProperty("state", out var state))
            {
                if (state.ValueKind == JsonValueKind.Object)
                {
                    status = ReadString(state, "status") ?? "";
                    summary = ReadString(state, "output") ?? "";
                    if (summary.Length == 0 && state.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    {
                        summary = error.ValueKind == JsonValueKind.String
                            ? error.GetString() ?? ""
                            : ReadString(error, "message") ?? error.ToString();
                    }
                    if (summary.Length == 0)
                        summary = ReadString(state, "title") ?? "";
                    // v2 tool states carry the result as a content array instead of a plain output string.
                    if (summary.Length == 0 && state.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        var joined = new StringBuilder();
                        foreach (var item in content.EnumerateArray())
                        {
                            var itemText = ReadString(item, "text");
                            if (!string.IsNullOrWhiteSpace(itemText))
                                joined.Append(itemText);
                        }
                        summary = joined.ToString();
                    }
                    if (state.TryGetProperty("input", out var inputEl) && inputEl.ValueKind != JsonValueKind.Null)
                        toolInput = inputEl.ValueKind == JsonValueKind.String ? inputEl.GetString() : inputEl.ToString();
                    // Only v1 tool states carry start/end timestamps; v2 omits them.
                    if (!isV2 && state.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object
                        && time.TryGetProperty("start", out var startEl) && startEl.ValueKind == JsonValueKind.Number
                        && time.TryGetProperty("end", out var endEl) && endEl.ValueKind == JsonValueKind.Number)
                    {
                        var ms = endEl.GetInt64() - startEl.GetInt64();
                        if (ms > 0)
                            durationMs = (int)ms;
                    }
                }
                else if (state.ValueKind == JsonValueKind.String)
                {
                    status = state.GetString() ?? "";
                }
            }

            summary = summary.Replace('\r', ' ').Replace('\n', ' ').Trim();
            var bodySummary = summary.Length > 240 ? summary[..240] : summary;
            var toolResult = summary.Length > 4000 ? summary[..4000] : summary;

            var body = bodySummary.Length == 0 ? name + "\n" + status : name + "\n" + status + "\n" + bodySummary;
            list.Add(new OpenCodeMessagePart(role, "tool", body, id, complete, name, status, toolInput, toolResult, durationMs));
            return;
        }

        var text = ReadString(part, "text") ?? ReadString(part, "content");
        if (string.IsNullOrWhiteSpace(text))
            return;
        var normalized = kind is "reasoning" or "thinking" ? "reasoning" : kind;
        list.Add(new OpenCodeMessagePart(role, normalized, text, id, complete));
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static OpenCodeSessionUsage ParseUsage(JsonElement root)
    {
        var promptTokens = 0;
        var completionTokens = 0;
        var assistantMessages = 0;
        var totalCost = 0.0;
        if (MessageArray(root) is { } messages)
        {
            foreach (var message in messages.EnumerateArray())
                (assistantMessages, promptTokens, completionTokens, totalCost) = AddMessageUsage(message, assistantMessages, promptTokens, completionTokens, totalCost);
        }
        return new OpenCodeSessionUsage(promptTokens, completionTokens, assistantMessages, totalCost);
    }

    private static OpenCodeSessionUsage ParseUsage(IReadOnlyList<string> rawMessages)
    {
        var assistantMessages = 0;
        var promptTokens = 0;
        var completionTokens = 0;
        var totalCost = 0.0;
        foreach (var raw in rawMessages)
        {
            using var doc = JsonDocument.Parse(raw);
            (assistantMessages, promptTokens, completionTokens, totalCost) =
                AddMessageUsage(doc.RootElement, assistantMessages, promptTokens, completionTokens, totalCost);
        }
        return new OpenCodeSessionUsage(promptTokens, completionTokens, assistantMessages, totalCost);
    }


    private static (int AssistantMessages, int PromptTokens, int CompletionTokens, double TotalCost) AddMessageUsage(JsonElement message, int assistantMessages, int promptTokens, int completionTokens, double totalCost)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return (assistantMessages, promptTokens, completionTokens, totalCost);
        JsonElement info = default;
        if (message.TryGetProperty("info", out var infoElement) && infoElement.ValueKind == JsonValueKind.Object)
            info = infoElement;
        else if (message.TryGetProperty("type", out _))
            info = message;
        string? role = null;
        if (info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("role", out var infoRole) && infoRole.ValueKind == JsonValueKind.String)
                role = infoRole.GetString();
            else if (info.TryGetProperty("type", out var infoType) && infoType.ValueKind == JsonValueKind.String)
                role = infoType.GetString();
        }
        if (role is null && message.TryGetProperty("role", out var topRole) && topRole.ValueKind == JsonValueKind.String)
            role = topRole.GetString();
        if (role != "assistant")
            return (assistantMessages, promptTokens, completionTokens, totalCost);
        assistantMessages++;
        var usageSource = info.ValueKind == JsonValueKind.Object ? info : message;
        if (usageSource.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
        {
            promptTokens += ReadInt(tokens, "input");
            completionTokens += ReadInt(tokens, "output");
            completionTokens += ReadInt(tokens, "reasoning");
            if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            {
                promptTokens += ReadInt(cache, "read");
                promptTokens += ReadInt(cache, "write");
            }
        }
        if (usageSource.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number && cost.TryGetDouble(out var parsedCost))
            totalCost += parsedCost;
        return (assistantMessages, promptTokens, completionTokens, totalCost);
    }

    private static int ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var number))
            return number;
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        await KillProcessAsync();
        _http.Dispose();
        _streamHttp.Dispose();
    }
}
/// <summary>
/// Whether the local OpenCode process is running and healthy.
/// </summary>
public readonly record struct OpenCodeServeStatus(bool Available, bool Healthy, string? Version, string? Error)
{
    public static OpenCodeServeStatus Ok(string? version) => new(true, true, version, null);
    public static OpenCodeServeStatus Missing(string error) => new(false, false, null, error);
}
/// <summary>
/// One chat part read back from OpenCode: role, kind, body, optional external id, and a completeness flag.
/// </summary>
public readonly record struct OpenCodeMessagePart(
    string Role,
    string Kind,
    string Body,
    string? ExternalId,
    bool Complete = true,
    string? Tool = null,
    string? ToolStatus = null,
    string? ToolInput = null,
    string? ToolResult = null,
    int? DurationMs = null);
