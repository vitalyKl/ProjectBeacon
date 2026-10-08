namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

public sealed record OpenCodeSessionUsage(int PromptTokens, int CompletionTokens, int AssistantMessages, double TotalCost);

public sealed class ClientOpenCodeServe : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private Process? _process;
    private const int MaxRestarts = 5;
    private int _unhealthyPolls;
    private int _restarts;
    private Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private string? _cwd;
    private int _port = 4096;
    private bool _runningFake;

    public OpenCodeServeStatus Status { get; private set; } = OpenCodeServeStatus.Missing("opencode is not started on this device.");
    public int Port => _port;
    public string? Cwd => _cwd;
    internal int StartCount { get; private set; }
    internal bool SkipRealProcess { get; set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendAsync { get; set; }

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
        var bin = WorkstationProbe.Which("opencode") ?? WorkstationProbe.Which("opencode.exe");
        if (string.IsNullOrWhiteSpace(bin) && !SkipRealProcess)
        {
            await KillProcessAsync();
            Status = OpenCodeServeStatus.Missing("opencode binary not found on this device.");
            return;
        }

        if (!string.Equals(next, _cwd, StringComparison.OrdinalIgnoreCase) || !IsRunning)
        {
            _restarts = 0;
            await KillProcessAsync();
        }

        _cwd = next;
        if (string.IsNullOrWhiteSpace(_cwd))
        {
            Status = OpenCodeServeStatus.Missing("No project folder to start OpenCode in.");
            return;
        }

        Directory.CreateDirectory(_cwd);
        EnsureProcess(bin ?? "opencode");
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
            Status = new OpenCodeServeStatus(Status.Available, false, Status.Version, $"opencode restart limit ({MaxRestarts}).");
            return;
        }
        _restarts++;
        await KillProcessAsync();
        EnsureProcess(bin ?? "opencode");
        await PollAsync(ct);
    }

    public async Task<string> CreateSessionAsync(string title, CancellationToken ct)
    {
        if (SkipRealProcess)
            return "oc_test";
        using var response = await Send(HttpMethod.Post, "/session", new { title }, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ReadId(doc.RootElement) ?? throw new InvalidOperationException("OpenCode session id missing.");
    }

    public async Task PromptAsync(string sessionId, string text, string? model, Application.Runtime.AgentPromptControls? controls, CancellationToken ct)
    {
        if (SkipRealProcess)
            return;
        var body = new Dictionary<string, object?>
        {
            ["parts"] = new object[] { new { type = "text", text } }
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            if (model.Contains('/', StringComparison.Ordinal))
            {
                var i = model.IndexOf('/');
                body["model"] = new { providerID = model[..i], modelID = model[(i + 1)..] };
            }
            else
            {
                body["model"] = new { providerID = "opencode", modelID = model };
            }
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
        using var response = await Send(HttpMethod.Post, $"/session/{sessionId}/abort", new { }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<OpenCodeMessagePart>> ListPartsAsync(string sessionId, CancellationToken ct)
    {
        if (SkipRealProcess)
            return [new OpenCodeMessagePart("assistant", "text", "ok", "fake-1")];
        using var response = await Send(HttpMethod.Get, $"/session/{sessionId}/message", null, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseParts(doc.RootElement);
    }

    public async Task<OpenCodeSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
    {
        if (SkipRealProcess)
            return new OpenCodeSessionUsage(12, 34, 1, 0.0001);

        using var response = await Send(HttpMethod.Get, $"/session/{Uri.EscapeDataString(sessionId)}/message", null, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseUsage(doc.RootElement);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_port}{path}");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (SendAsync is not null)
            return await SendAsync(request, ct);
        return await _http.SendAsync(request, ct);
    }

    private bool IsRunning => SkipRealProcess ? _runningFake : _process is { HasExited: false };

    private void EnsureProcess(string bin)
    {
        if (IsRunning)
            return;
        StartCount++;
        if (SkipRealProcess)
        {
            _runningFake = true;
            Status = OpenCodeServeStatus.Ok(null);
            return;
        }
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
        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                Status = OpenCodeServeStatus.Missing("opencode serve failed to start.");
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Dispose();
            Status = OpenCodeServeStatus.Missing($"opencode serve failed to start: {ex.Message}");
            return;
        }
        if (!ProcessControl.TryBeginDrain(process))
        {
            process.Dispose();
            Status = OpenCodeServeStatus.Missing("opencode serve failed to drain output.");
            return;
        }
        _process = process;
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
        await ProcessControl.KillAsync(process);
        try { process.Dispose(); } catch { }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (SkipRealProcess)
        {
            Status = OpenCodeServeStatus.Ok(null);
            return;
        }
        try
        {
            using var health = await Send(HttpMethod.Get, "/global/health", null, ct);
            if (!health.IsSuccessStatusCode)
            {
                Status = new OpenCodeServeStatus(true, false, null, $"/global/health {(int)health.StatusCode}");
                return;
            }
            var json = await health.Content.ReadAsStringAsync(ct);
            string? version = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            }
            catch (JsonException)
            {
            }
            Status = OpenCodeServeStatus.Ok(version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = new OpenCodeServeStatus(true, false, null, $"opencode serve not reachable: {ex.Message}");
        }
    }

    private static string? ReadId(JsonElement root)
    {
        if (root.TryGetProperty("id", out var id))
            return id.GetString();
        if (root.TryGetProperty("info", out var info) && info.TryGetProperty("id", out var nested))
            return nested.GetString();
        return null;
    }

    private static IReadOnlyList<OpenCodeMessagePart> ParseParts(JsonElement root)
    {
        var list = new List<OpenCodeMessagePart>();
        var messages = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray()
            : root.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray()
            : default;
        if (messages.Equals(default(JsonElement.ArrayEnumerator)) && root.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var message in root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : messages)
        {
            var role = "assistant";
            if (message.TryGetProperty("info", out var info) && info.TryGetProperty("role", out var roleEl))
                role = roleEl.GetString() ?? role;
            else if (message.TryGetProperty("role", out var r))
                role = r.GetString() ?? role;
            if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in parts.EnumerateArray())
                AddPart(list, role, part);
        }
        return list;
    }

    private static void AddPart(List<OpenCodeMessagePart> list, string role, JsonElement part)
    {
        var kind = part.TryGetProperty("type", out var t) ? t.GetString() ?? "text" : "text";
        var id = part.TryGetProperty("id", out var pid) ? pid.GetString() : null;
        if (kind is "tool" or "tool-call" or "tool_use" or "tool_call")
        {
            var name = ReadString(part, "tool") ?? ReadString(part, "name") ?? "tool";
            var status = "";
            var summary = "";
            if (part.TryGetProperty("state", out var state))
            {
                if (state.ValueKind == JsonValueKind.Object)
                {
                    status = ReadString(state, "status") ?? "";
                    summary = ReadString(state, "output") ?? ReadString(state, "error") ?? ReadString(state, "title") ?? "";
                }
                else if (state.ValueKind == JsonValueKind.String)
                {
                    status = state.GetString() ?? "";
                }
            }

            summary = summary.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (summary.Length > 240)
                summary = summary[..240];
            var body = summary.Length == 0 ? name + "\n" + status : name + "\n" + status + "\n" + summary;
            list.Add(new OpenCodeMessagePart(role, "tool", body, id));
            return;
        }

        var text = ReadString(part, "text") ?? ReadString(part, "content");
        if (string.IsNullOrWhiteSpace(text))
            return;
        var normalized = kind is "reasoning" or "thinking" ? "reasoning" : kind;
        list.Add(new OpenCodeMessagePart(role, normalized, text, id));
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
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in root.EnumerateArray())
                (assistantMessages, promptTokens, completionTokens, totalCost) = AddMessageUsage(message, assistantMessages, promptTokens, completionTokens, totalCost);
        }
        else if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
                (assistantMessages, promptTokens, completionTokens, totalCost) = AddMessageUsage(message, assistantMessages, promptTokens, completionTokens, totalCost);
        }
        return new OpenCodeSessionUsage(promptTokens, completionTokens, assistantMessages, totalCost);
    }

    private static (int AssistantMessages, int PromptTokens, int CompletionTokens, double TotalCost) AddMessageUsage(JsonElement message, int assistantMessages, int promptTokens, int completionTokens, double totalCost)
    {
        string? role = null;
        JsonElement info = default;
        if (message.TryGetProperty("info", out var infoElement) && infoElement.ValueKind == JsonValueKind.Object)
            info = infoElement;
        if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("role", out var infoRole) && infoRole.ValueKind == JsonValueKind.String)
            role = infoRole.GetString();
        else if (message.TryGetProperty("role", out var topRole) && topRole.ValueKind == JsonValueKind.String)
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
    }
}

public readonly record struct OpenCodeServeStatus(bool Available, bool Healthy, string? Version, string? Error)
{
    public static OpenCodeServeStatus Ok(string? version) => new(true, true, version, null);
    public static OpenCodeServeStatus Missing(string error) => new(false, false, null, error);
}

public readonly record struct OpenCodeMessagePart(string Role, string Kind, string Body, string? ExternalId);
