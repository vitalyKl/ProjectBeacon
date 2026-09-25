namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

public sealed class ClientOpenCodeServe : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private Process? _process;
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
        KillProcess();
        await TickAsync(cwd, ct);
    }

    public async Task TickAsync(string? cwd, CancellationToken ct)
    {
        var next = string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFullPath(cwd);
        var bin = WorkstationActions.Which("opencode") ?? WorkstationActions.Which("opencode.exe");
        if (string.IsNullOrWhiteSpace(bin) && !SkipRealProcess)
        {
            KillProcess();
            Status = OpenCodeServeStatus.Missing("opencode binary not found on this device.");
            return;
        }

        if (!string.Equals(next, _cwd, StringComparison.OrdinalIgnoreCase) || !IsRunning)
            KillProcess();

        _cwd = next;
        if (string.IsNullOrWhiteSpace(_cwd))
        {
            Status = OpenCodeServeStatus.Missing("No project folder to start OpenCode in.");
            return;
        }

        Directory.CreateDirectory(_cwd);
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

    public async Task PromptAsync(string sessionId, string text, string? model, CancellationToken ct)
    {
        if (SkipRealProcess)
            return;
        var body = new Dictionary<string, object?>
        {
            ["parts"] = new object[] { new { type = "text", text } }
        };
        if (!string.IsNullOrWhiteSpace(model) && model.Contains('/', StringComparison.Ordinal))
        {
            var i = model.IndexOf('/');
            body["model"] = new { providerID = model[..i], modelID = model[(i + 1)..] };
        }
        using var response = await Send(HttpMethod.Post, $"/session/{sessionId}/prompt_async", body, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
            response.EnsureSuccessStatusCode();
    }

    public async Task AbortAsync(string sessionId, CancellationToken ct)
    {
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
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    private void KillProcess()
    {
        if (SkipRealProcess)
        {
            _runningFake = false;
            return;
        }
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            try { _process.WaitForExit(2000); } catch { }
        }
        _process?.Dispose();
        _process = null;
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
            {
                var kind = part.TryGetProperty("type", out var t) ? t.GetString() ?? "text" : "text";
                var text = part.TryGetProperty("text", out var tx) ? tx.GetString()
                    : part.TryGetProperty("content", out var c) ? c.GetString() : "";
                var id = part.TryGetProperty("id", out var pid) ? pid.GetString() : null;
                if (!string.IsNullOrWhiteSpace(text))
                    list.Add(new OpenCodeMessagePart(role, kind, text!, id));
            }
        }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        KillProcess();
        _http.Dispose();
        await Task.CompletedTask;
    }
}

public readonly record struct OpenCodeServeStatus(bool Available, bool Healthy, string? Version, string? Error)
{
    public static OpenCodeServeStatus Ok(string? version) => new(true, true, version, null);
    public static OpenCodeServeStatus Missing(string error) => new(false, false, null, error);
}

public readonly record struct OpenCodeMessagePart(string Role, string Kind, string Body, string? ExternalId);
