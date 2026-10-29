namespace ProjectBeacon.Cli.Client;

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Runtime;

internal interface IDaemonChatSession
{
    Task<(bool Ok, string? Result, string? Error)> EnsureAsync(string cwd, string payload, CancellationToken ct);
    Task<(bool Ok, string? Result, string? Error)> PromptAsync(string cwd, string payload, CancellationToken ct);
    Task<(bool Ok, string? Result, string? Error)> AbortAsync(string payload, CancellationToken ct);
    Task<(bool Ok, string? Result, string? Error)> SwitchAgentAsync(string payload, CancellationToken ct);
    Task<(bool Ok, string? Result, string? Error)> ReplyFormAsync(string payload, CancellationToken ct);
}

internal sealed class DaemonChatSession : IDaemonChatSession
{
    private readonly HttpClient _http;
    private readonly ClientLlamaSwap _llama;
    private readonly IDaemonOpenCodeGate _gate;
    private readonly IDaemonLlamaSync _llamaSync;
    private readonly IDaemonRuntimeState _state;
    private readonly DaemonTiming _timing;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeTurns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _abortRequested = new(StringComparer.Ordinal);

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
            var readiness = await _gate.EnsureReadyAsync(cwd, ct);
            if (!readiness.Ready)
                return (false, (string?)null, readiness.Error ?? "The selected agent runtime is not ready.");
            var id = await _gate.Runtime.CreateSessionAsync(title, ct);
            return (true, JsonSerializer.Serialize(new { sessionId = id, cwd = readiness.WorkingDirectory ?? Path.GetFullPath(cwd) }), (string?)null);
        }, ct);
    }

    public async Task<(bool Ok, string? Result, string? Error)> PromptAsync(string cwd, string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var externalId = doc.RootElement.TryGetProperty("externalSessionId", out var e) ? e.GetString() : null;
        var chatId = doc.RootElement.TryGetProperty("chatSessionId", out var c) ? c.GetGuid() : Guid.Empty;
        var text = doc.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() : null;
        var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
        var effort = doc.RootElement.TryGetProperty("reasoningEffort", out var eff) ? eff.GetString() : null;
        var contextLimit = doc.RootElement.TryGetProperty("contextLimit", out var limit)
            && limit.ValueKind == JsonValueKind.Number && limit.TryGetInt32(out var configuredLimit) && configuredLimit > 0
                ? configuredLimit
                : (int?)null;
        // Carries the session's project so the daemon's parts/idle writes are scoped to it, not the owner's default scope.
        var projectId = doc.RootElement.TryGetProperty("projectId", out var p) && p.TryGetGuid(out var pid) ? pid : Guid.Empty;
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(text) || chatId == Guid.Empty)
            return (false, null, "chatSessionId, externalSessionId, and text are required.");

        using var activeTurn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_activeTurns.TryAdd(externalId, activeTurn))
            return (false, null, "A prompt is already running for this session.");
        try
        {
            return await _gate.RunExclusiveAsync(
                () => PromptLockedAsync(cwd, externalId, chatId, text, model, effort, projectId, contextLimit, activeTurn.Token),
                activeTurn.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && activeTurn.IsCancellationRequested)
        {
            return (false, null, "Chat prompt was aborted.");
        }
        finally
        {
            if (_activeTurns.TryGetValue(externalId, out var current) && ReferenceEquals(current, activeTurn))
                _activeTurns.TryRemove(externalId, out _);
            _abortRequested.TryRemove(externalId, out _);
        }
    }

    public async Task<(bool Ok, string? Result, string? Error)> AbortAsync(string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var externalId = doc.RootElement.TryGetProperty("externalSessionId", out var e) ? e.GetString() : null;
        if (string.IsNullOrWhiteSpace(externalId))
            return (false, null, "externalSessionId is required.");

        if (_activeTurns.TryGetValue(externalId, out var activeTurn))
        {
            _abortRequested[externalId] = 0;
            activeTurn.Cancel();
        }
        await _gate.Runtime.AbortAsync(externalId, ct);
        return (true, "{}", null);
    }

    public async Task<(bool Ok, string? Result, string? Error)> SwitchAgentAsync(string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var externalId = doc.RootElement.TryGetProperty("externalSessionId", out var e) ? e.GetString() : null;
        var agent = doc.RootElement.TryGetProperty("agent", out var a) ? a.GetString() : null;
        var chatId = doc.RootElement.TryGetProperty("chatSessionId", out var c) && c.TryGetGuid(out var cid) ? cid : Guid.Empty;
        var projectId = doc.RootElement.TryGetProperty("projectId", out var p) && p.TryGetGuid(out var pid) ? pid : Guid.Empty;
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(agent))
            return (false, null, "externalSessionId and agent are required.");
        try
        {
            // No gate: the switch is a plain serve HTTP call and must not wait out an active turn.
            if (_gate.Runtime is IAgentRuntimeCapabilities capabilities)
            {
                if (!capabilities.SupportsAgentSwitch)
                    throw new InvalidOperationException("Agent switching is not supported by the selected runtime.");
                await capabilities.SwitchAgentAsync(externalId, agent, ct);
            }
            else
            {
                // Compatibility path for existing runtime/test adapters; the production OpenCode
                // runtime implements capabilities and never reaches this branch.
                await _gate.OpenCode.SwitchAgentAsync(externalId, agent, ct);
            }
            return (true, "{}", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = ClipError(ex.Message);
            // Surface the failure in the chat: the agent may simply not exist in the project config.
            if (chatId != Guid.Empty)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{agent}:{error}")))[..16];
                await PostChatAsync($"/v1/chat/sessions/{chatId}/parts", new
                {
                    role = "assistant",
                    kind = "error",
                    body = error,
                    externalId = $"agent:{hash}"
                }, projectId, ct);
            }
            return (false, null, error);
        }
    }

    public async Task<(bool Ok, string? Result, string? Error)> ReplyFormAsync(string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var externalId = doc.RootElement.TryGetProperty("externalSessionId", out var e) ? e.GetString() : null;
        var formId = doc.RootElement.TryGetProperty("formId", out var f) ? f.GetString() : null;
        var answerJson = doc.RootElement.TryGetProperty("answerJson", out var ans) ? ans.GetString() : null;
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(formId) || string.IsNullOrWhiteSpace(answerJson))
            return (false, null, "externalSessionId, formId, and answerJson are required.");
        try
        {
            // No gate: a queued prompt turn holds it, but the model blocks on the form answer.
            bool settled;
            if (_gate.Runtime is IAgentRuntimeCapabilities capabilities)
            {
                if (!capabilities.SupportsForms)
                    throw new InvalidOperationException("Interactive forms are not supported by the selected runtime.");
                settled = await capabilities.ReplyFormAsync(externalId, formId, answerJson, ct);
            }
            else
            {
                settled = await _gate.OpenCode.ReplyFormAsync(externalId, formId, answerJson, ct);
            }
            return settled
                ? (true, "{}", null)
                : (true, JsonSerializer.Serialize(new { alreadySettled = true }), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null, ClipError(ex.Message));
        }
    }

    private async Task<(bool Ok, string? Result, string? Error)> PromptLockedAsync(
        string cwd, string externalId, Guid chatId, string text, string? model, string? effort,
        Guid projectId, int? configuredContextLimit, CancellationToken ct)
    {
        var runtime = _gate.Runtime;
        var capabilities = runtime as IAgentRuntimeCapabilities;
        var readiness = await _gate.EnsureReadyAsync(cwd, ct);
        if (!readiness.Ready)
            return (false, null, readiness.Error ?? "The selected agent runtime is not ready.");
        if (_llama.UseOwnSwapper && model is not null)
            await _llamaSync.RunLockedAsync(() => _llama.EnsureSwapModelAsync(DaemonPayload.ExtractSwapName(model), ct), ct);
        // Baseline: the last assistant message before the prompt; an ended turn from an
        // earlier prompt must not close this one.
        var baselineMessageId = (await runtime.GetTurnStatusAsync(externalId, ct))?.MessageId;
        // Open the live text stream before the prompt so no delta is missed. v1 servers have no
        // event feed (the stream method is a no-op for them) and polling stays the backstop.
        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamBuffers = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        Task streamTask;
        if (capabilities is { SupportsLiveDeltas: true })
        {
            streamTask = capabilities.StreamTextAsync(externalId, StreamDeltaAsync, streamCts.Token);
        }
        else if (capabilities is null)
        {
            // Compatibility path for pre-capability runtimes and existing test adapters.
            streamTask = _gate.OpenCode.StreamSessionTextAsync(externalId, StreamDeltaAsync, streamCts.Token);
        }
        else
        {
            streamTask = Task.CompletedTask;
        }

        Task StreamDeltaAsync(string kind, string messageId, int ordinal, string delta, CancellationToken streamCt)
        {
            var key = messageId + ":" + kind + ":" + ordinal;
            var body = streamBuffers.AddOrUpdate(key, delta, (_, old) => old + delta);
            return PostChatAsync($"/v1/chat/sessions/{chatId}/parts", new
            {
                role = "assistant",
                kind,
                body,
                externalId = key,
                complete = false
            }, projectId, streamCt);
        }
        var normalizedEffort = string.IsNullOrWhiteSpace(effort) || string.Equals(effort, "default", StringComparison.OrdinalIgnoreCase)
            ? null
            : effort;
        var controls = normalizedEffort is null && configuredContextLimit is not > 0
            ? null
            : new AgentPromptControls(null, normalizedEffort, null) { ContextLimit = configuredContextLimit };
        await runtime.SendPromptAsync(externalId, text, model, ct, controls);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenForms = new HashSet<string>(StringComparer.Ordinal);
        var maxQuiet = Math.Max(1, (int)Math.Ceiling(_timing.ChatIdleTimeout / _timing.ChatPollInterval));
        var maxIterations = Math.Max(1, (int)Math.Ceiling(_timing.ChatMaxDuration / _timing.ChatPollInterval));
        var quiet = 0;
        var assistantParts = 0;
        var idle = false;
        AgentTurnStatus? lastTurn = null;
        try
        {
            for (var i = 0; i < maxIterations && !ct.IsCancellationRequested; i++)
            {
                var parts = await CollectPartsAsync(runtime, externalId, ct);
                var added = 0;
                foreach (var part in parts)
                {
                    if (string.Equals(part.Role, "user", StringComparison.OrdinalIgnoreCase) || !part.Complete)
                        continue;
                    var key = part.ExternalId ?? $"{part.Role}:{part.Kind}:{part.Body}";
                    if (!seen.Add(key))
                        continue;
                    added++;
                    await PostChatAsync($"/v1/chat/sessions/{chatId}/parts", new
                    {
                        role = part.Role,
                        kind = part.Kind,
                        body = part.Body,
                        externalId = part.ExternalId,
                        tool = part.Tool,
                        toolStatus = part.ToolStatus,
                        toolInput = part.ToolInput,
                        toolResult = part.ToolResult,
                        durationMs = part.DurationMs,
                        complete = part.Complete
                    }, projectId, ct);
                    if (string.Equals(part.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                        assistantParts++;
                }
                // Surface model questions: v2 forms live outside the message list, so poll them
                // while the turn runs instead of waiting for the durable parts.
                if (i % 8 == 0)
                    await SurfacePendingFormsAsync(capabilities, externalId, chatId, projectId, seenForms, ct);
                quiet = added > 0 ? 0 : quiet + 1;
                // The turn ends only when the last assistant message finishes without pending tool calls;
                // a tool-calls finish means the agent keeps working, so quiet polls are not idle.
                var turn = await runtime.GetTurnStatusAsync(externalId, ct);
                lastTurn = turn;
                // Fresh means a new assistant message appeared after the baseline.
                var fresh = turn is not null && turn.MessageId is not null && turn.MessageId != baselineMessageId;
                if (turn is { Ended: true } && fresh)
                    idle = true;
                else if (turn is null && quiet >= maxQuiet && assistantParts > 0)
                    // The quiet path must not end a turn before the first assistant part: a slow
                    // first token (model swap, context compaction) can outlast ChatIdleTimeout.
                    idle = true;
                if (idle)
                    break;
                await _timing.DelayAsync(_timing.ChatPollInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Explicit ChatAbort sends the interrupt itself. Other cancellations (e.g. daemon
            // shutdown) must still interrupt the remote generation before unwinding.
            if (!_abortRequested.TryRemove(externalId, out _))
            {
                try { await runtime.AbortAsync(externalId, CancellationToken.None); } catch { }
            }
            throw;
        }
        finally
        {
            streamCts.Cancel();
            try { await streamTask; } catch { }
        }
        if (idle)
        {
            if (lastTurn is { Error: not null, MessageId: not null } && lastTurn.MessageId != baselineMessageId)
                await PostErrorPartAsync(chatId, lastTurn, projectId, ct);
            _state.Log(lastTurn is { Error: not null }
                ? $"chat prompt done: {seen.Count} parts, idle with error: {ClipError(lastTurn.Error!)}"
                : $"chat prompt done: {seen.Count} parts, idle");
        }
        else
        {
            _state.Log($"chat prompt interrupted: max duration {_timing.ChatMaxDuration.TotalSeconds:0}s reached, {seen.Count} parts");
            try { await runtime.AbortAsync(externalId, CancellationToken.None); } catch { }
        }
        try
        {
            var usage = await runtime.ReadUsageAsync(externalId, ct);
            var contextLimit = configuredContextLimit is > 0
                ? configuredContextLimit
                : capabilities is not null
                    ? await capabilities.ReadContextLimitAsync(model, ct)
                    : await _gate.OpenCode.ReadContextLimitAsync(model, ct);
            await PostChatAsync($"/v1/chat/sessions/{chatId}/usage", new
            {
                promptTokens = usage.PromptTokens,
                completionTokens = usage.CompletionTokens,
                assistantMessages = usage.AssistantMessages,
                totalCost = usage.TotalCost,
                generationSeconds = usage.GenerationSeconds,
                contextLimit
            }, projectId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state.Log($"chat usage report failed: {ex.Message}");
        }
        await PostChatAsync($"/v1/chat/sessions/{chatId}/idle", new { }, projectId, ct);
        return (true, JsonSerializer.Serialize(new { sessionId = externalId, interrupted = !idle }), null);
    }

    private static string ClipError(string error) => error.Length <= 200 ? error : error[..200];

    private async Task PostErrorPartAsync(Guid chatId, AgentTurnStatus turn, Guid projectId, CancellationToken ct)
    {
        // Stable external id derived from the error text so a repeated identical error dedupes on the API.
        var body = ClipError(turn.Error!);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16];
        await PostChatAsync($"/v1/chat/sessions/{chatId}/parts", new
        {
            role = "assistant",
            kind = "error",
            body,
            externalId = $"error:{hash}"
        }, projectId, ct);
    }

    private async Task SurfacePendingFormsAsync(
        IAgentRuntimeCapabilities? capabilities, string externalId, Guid chatId, Guid projectId, HashSet<string> seenForms, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<AgentRuntimeForm> forms;
            if (capabilities is { SupportsForms: true })
                forms = await capabilities.ListPendingFormsAsync(externalId, ct);
            else if (capabilities is null)
                forms = (await _gate.OpenCode.ListPendingFormsAsync(externalId, ct))
                    .Select(form => new AgentRuntimeForm(form.Id, form.Title, form.Metadata, form.FieldsJson))
                    .ToArray();
            else
                return;

            foreach (var form in forms)
            {
                if (!seenForms.Add(form.Id))
                    continue;
                var body = JsonSerializer.Serialize(new
                {
                    id = form.Id,
                    title = form.Title,
                    metadata = form.Metadata,
                    fields = JsonNode.Parse(form.FieldsJson)
                });
                await PostChatAsync($"/v1/chat/sessions/{chatId}/parts", new
                {
                    role = "assistant",
                    kind = "question",
                    body,
                    externalId = $"form:{form.Id}",
                    complete = true
                }, projectId, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _state.Log($"chat form poll failed: {ex.Message}");
        }
    }

    private Task PostChatAsync(string url, object body, Guid projectId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (projectId != Guid.Empty)
            request.Headers.TryAddWithoutValidation("X-Project-Id", projectId.ToString());
        return _http.SendAsync(request, ct);
    }

    private static async Task<IReadOnlyList<AgentMessagePart>> CollectPartsAsync(IAgentRuntime runtime, string sessionId, CancellationToken ct)
    {
        var parts = new List<AgentMessagePart>();
        await foreach (var part in runtime.StreamPartsAsync(sessionId, ct))
            parts.Add(part);
        return parts;
    }
}
