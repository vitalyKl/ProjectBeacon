namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Json;
using System.Text.Json;
using Application.Runtime;

internal interface IDaemonChatSession
{
    Task<(bool Ok, string? Result, string? Error)> EnsureAsync(string cwd, string payload, CancellationToken ct);
    Task<(bool Ok, string? Result, string? Error)> PromptAsync(string cwd, string payload, CancellationToken ct);
    Task<(bool Ok, string? Result, string? Error)> AbortAsync(string payload, CancellationToken ct);
}

internal sealed class DaemonChatSession : IDaemonChatSession
{
    private readonly HttpClient _http;
    private readonly ClientLlamaSwap _llama;
    private readonly IDaemonOpenCodeGate _gate;
    private readonly IDaemonLlamaSync _llamaSync;
    private readonly IDaemonRuntimeState _state;
    private readonly DaemonTiming _timing;

    public DaemonChatSession(
        DaemonDependencies deps,
        IDaemonOpenCodeGate gate,
        IDaemonLlamaSync llamaSync,
        IDaemonRuntimeState state,
        DaemonTiming timing)
    {
        _http = deps.Http;
        _llama = deps.Llama;
        _gate = gate;
        _llamaSync = llamaSync;
        _state = state;
        _timing = timing;
    }

    public async Task<(bool Ok, string? Result, string? Error)> EnsureAsync(string cwd, string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "Chat" : "Chat";
        return await _gate.RunExclusiveAsync(async () =>
        {
            await _gate.OpenCode.TickAsync(cwd, ct);
            if (!_gate.OpenCode.Status.Healthy)
                return (false, (string?)null, _gate.OpenCode.Status.Error ?? "OpenCode is not running.");
            var id = await _gate.Runtime.CreateSessionAsync(title, ct);
            return (true, JsonSerializer.Serialize(new { sessionId = id, cwd = _gate.OpenCode.Cwd }), (string?)null);
        }, ct);
    }

    public async Task<(bool Ok, string? Result, string? Error)> PromptAsync(string cwd, string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var externalId = doc.RootElement.TryGetProperty("externalSessionId", out var e) ? e.GetString() : null;
        var chatId = doc.RootElement.TryGetProperty("chatSessionId", out var c) ? c.GetGuid() : Guid.Empty;
        var text = doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : null;
        var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(text) || chatId == Guid.Empty)
            return (false, null, "chatSessionId, externalSessionId, and text are required.");
        return await _gate.RunExclusiveAsync(
            () => PromptLockedAsync(cwd, externalId, chatId, text, model, ct),
            ct);
    }

    public async Task<(bool Ok, string? Result, string? Error)> AbortAsync(string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var externalId = doc.RootElement.TryGetProperty("externalSessionId", out var e) ? e.GetString() : null;
        if (string.IsNullOrWhiteSpace(externalId))
            return (false, null, "externalSessionId is required.");
        await _gate.Runtime.AbortAsync(externalId, ct);
        return (true, "{}", null);
    }

    private async Task<(bool Ok, string? Result, string? Error)> PromptLockedAsync(
        string cwd, string externalId, Guid chatId, string text, string? model, CancellationToken ct)
    {
        var openCode = _gate.OpenCode;
        var runtime = _gate.Runtime;
        await openCode.TickAsync(cwd, ct);
        if (!openCode.Status.Healthy)
            return (false, null, openCode.Status.Error ?? "OpenCode is not running.");
        if (_llama.UseOwnSwapper && model is not null)
            await _llamaSync.RunLockedAsync(() => _llama.EnsureSwapModelAsync(DaemonPayload.ExtractSwapName(model), ct), ct);
        await runtime.SendPromptAsync(externalId, text, model, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maxQuiet = Math.Max(1, (int)Math.Ceiling(_timing.ChatIdleTimeout / _timing.ChatPollInterval));
        var maxIterations = Math.Max(1, (int)Math.Ceiling(_timing.ChatMaxDuration / _timing.ChatPollInterval));
        var quiet = 0;
        var idle = false;
        try
        {
            for (var i = 0; i < maxIterations && !ct.IsCancellationRequested; i++)
            {
                var parts = await CollectPartsAsync(runtime, externalId, ct);
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
                await _timing.DelayAsync(_timing.ChatPollInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            try { await runtime.AbortAsync(externalId, CancellationToken.None); } catch { }
            throw;
        }
        if (idle)
            _state.Log($"chat prompt done: {seen.Count} parts, idle");
        else
        {
            _state.Log($"chat prompt interrupted: max duration {_timing.ChatMaxDuration.TotalSeconds:0}s reached, {seen.Count} parts");
            try { await runtime.AbortAsync(externalId, CancellationToken.None); } catch { }
        }
        await _http.PostAsJsonAsync($"/v1/chat/sessions/{chatId}/idle", new { }, ct);
        return (true, JsonSerializer.Serialize(new { sessionId = externalId, interrupted = !idle }), null);
    }

    private static async Task<IReadOnlyList<AgentMessagePart>> CollectPartsAsync(IAgentRuntime runtime, string sessionId, CancellationToken ct)
    {
        var parts = new List<AgentMessagePart>();
        await foreach (var part in runtime.StreamPartsAsync(sessionId, ct))
            parts.Add(part);
        return parts;
    }
}
