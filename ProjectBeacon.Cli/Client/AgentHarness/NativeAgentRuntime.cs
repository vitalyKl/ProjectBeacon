namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ProjectBeacon.Application.Context;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;

/// <summary>
/// Workstation-local chat runtime. It owns session transcripts and the model/tool loop; it never
/// executes inside the Web, API, or Worker processes.
/// </summary>
internal sealed class NativeAgentRuntime : IAgentRuntime, IAgentRuntimeCapabilities
{
    private const int MaxAttempts = 3;
    private const int MaxAgentSteps = 12;
    private const int MaxToolResultCharacters = 16 * 1024;
    private readonly HttpClient _controlPlane;
    private readonly ClientLlamaSwap _llama;
    private readonly IDaemonLlamaSync _llamaSync;
    private readonly Func<WorkstationSettings> _loadSettings;
    private readonly Action<string>? _log;
    private readonly Func<HttpClient, string, string?, IChatProvider> _providerFactory;
    private readonly Func<AgentHarnessAuthorization, CancellationToken, Task<IMcpToolClient>> _mcpClientFactory;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    public NativeAgentRuntime(
        HttpClient controlPlane,
        ClientLlamaSwap llama,
        IDaemonLlamaSync llamaSync,
        Func<WorkstationSettings> loadSettings,
        Action<string>? log = null,
        Func<HttpClient, string, string?, IChatProvider>? providerFactory = null,
        Func<AgentHarnessAuthorization, CancellationToken, Task<IMcpToolClient>>? mcpClientFactory = null)
    {
        _controlPlane = controlPlane;
        _llama = llama;
        _llamaSync = llamaSync;
        _loadSettings = loadSettings;
        _log = log;
        _providerFactory = providerFactory ?? ((http, url, key) => new OpenAiCompatProvider(http, url, key));
        _mcpClientFactory = mcpClientFactory ?? StartMcpClientAsync;
    }

    private static async Task<IMcpToolClient> StartMcpClientAsync(
        AgentHarnessAuthorization authorization,
        CancellationToken ct)
        => await McpStdioClient.StartAsync(authorization, ct);

    public bool SupportsForms => false;
    public bool SupportsAgentSwitch => false;
    public bool SupportsReasoningEffort => true;
    public bool SupportsLiveDeltas => true;

    public async Task<string> CreateSessionAsync(string title, CancellationToken ct)
    {
        ThrowIfDisposed();
        var settings = _loadSettings();
        var state = new SessionState
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? "Chat" : title.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            TurnStatus = new AgentTurnStatus(true, "stop", null)
        };
        state.TranscriptPath = TranscriptPath(settings, Guid.Empty, state.Id);
        state.Messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "You are the ProjectBeacon workstation assistant. Be accurate, concise, and explicit about uncertainty. You can only use tools explicitly provided in this conversation." + Environment.NewLine + BeaconToolDiscipline.Rules
        });
        if (!_sessions.TryAdd(state.Id, state))
            throw new InvalidOperationException("Could not create a unique agent session.");
        await PersistAsync(state, ct);
        return state.Id;
    }

    public async Task SendPromptAsync(
        string sessionId,
        string text,
        string? model,
        CancellationToken ct,
        AgentPromptControls? controls = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Prompt text is required.", nameof(text));
        var session = await GetSessionAsync(sessionId, ct);
        var selectedModel = string.IsNullOrWhiteSpace(model) ? session.LastModel : model.Trim();
        if (string.IsNullOrWhiteSpace(selectedModel))
            throw new InvalidOperationException("Select a model before sending a prompt.");

        CancellationTokenSource turnCts;
        int turnNumber;
        int previousTurnNumber;
        AgentTurnStatus? previousTurnStatus;
        string? previousModel;
        int? previousContextLimit;
        JsonObject userMessage;
        lock (session.Sync)
        {
            if (session.ActiveTurn is not null)
                throw new InvalidOperationException("A prompt is already running for this session.");

            // The lock first confirms that no turn is active, then repairs any abandoned tool
            // calls and reserves the session. A concurrent prompt must never "recover" a live turn.
            RecoverInterruptedToolCalls(session);

            previousTurnNumber = session.TurnNumber;
            previousTurnStatus = session.TurnStatus;
            previousModel = session.LastModel;
            previousContextLimit = session.ContextLimit;

            turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            session.ActiveTurn = turnCts;
            session.LastModel = selectedModel;
            session.ContextLimit = controls?.ContextLimit is > 0 ? controls.ContextLimit : session.ContextLimit;
            turnNumber = ++session.TurnNumber;
            session.TurnStatus = new AgentTurnStatus(false, null, null, session.TurnStatus?.MessageId);
            userMessage = new JsonObject { ["role"] = "user", ["content"] = text };
            session.Messages.Add(userMessage);
        }

        try
        {
            await PersistAsync(session, ct);
        }
        catch
        {
            // Do not leave the session reserved when the user prompt could not be durably stored.
            lock (session.Sync)
            {
                if (ReferenceEquals(session.ActiveTurn, turnCts))
                {
                    session.ActiveTurn = null;
                    session.TurnNumber = previousTurnNumber;
                    session.TurnStatus = previousTurnStatus;
                    session.LastModel = previousModel;
                    session.ContextLimit = previousContextLimit;
                    session.Messages.Remove(userMessage);
                }
            }
            turnCts.Dispose();
            throw;
        }

        var task = Task.Run(() => RunTurnAsync(session, turnNumber, selectedModel, controls, turnCts.Token), CancellationToken.None);
        lock (session.Sync)
            session.ActiveTask = task;
    }

    public async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var session = await GetSessionAsync(sessionId, ct);
        AgentMessagePart[] snapshot;
        lock (session.Sync)
            snapshot = session.Parts.ToArray();
        foreach (var part in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return part;
        }
    }

    public async Task<AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct)
    {
        var session = await GetSessionAsync(sessionId, ct);
        lock (session.Sync)
            return session.TurnStatus;
    }

    public async Task AbortAsync(string sessionId, CancellationToken ct)
    {
        var session = await GetSessionAsync(sessionId, ct);
        CancellationTokenSource? active;
        Task? activeTask;
        lock (session.Sync)
        {
            active = session.ActiveTurn;
            activeTask = session.ActiveTask;
        }

        active?.Cancel();
        if (activeTask is not null)
            await activeTask.WaitAsync(ct);
    }

    public async Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
    {
        var session = await GetSessionAsync(sessionId, ct);
        lock (session.Sync)
            return session.Usage;
    }

    public Task<AgentRuntimeReadiness> EnsureReadyAsync(string? workingDirectory, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Task.FromResult(new AgentRuntimeReadiness(false, "A project working directory is required.", null));
        var fullPath = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(fullPath))
            return Task.FromResult(new AgentRuntimeReadiness(false, "The selected project directory does not exist.", fullPath));
        // Provider reachability is intentionally checked on the first request: this readiness
        // probe must not make a live network call or reject cloud providers because the local
        // llama-server is currently unloaded.
        var warning = _llama.NativeToolCallingWarning();
        if (!string.IsNullOrWhiteSpace(warning))
            _log?.Invoke(warning);
        return Task.FromResult(new AgentRuntimeReadiness(true, null, fullPath, warning));
    }

    public async Task StreamTextAsync(
        string sessionId,
        Func<string, string, int, string, CancellationToken, Task> onDelta,
        CancellationToken ct)
    {
        var session = await GetSessionAsync(sessionId, ct);
        int targetTurn;
        lock (session.Sync)
            targetTurn = session.TurnNumber + 1;

        await foreach (var item in session.Deltas.Reader.ReadAllAsync(ct))
        {
            if (item.TurnNumber < targetTurn)
                continue;
            if (item.TurnNumber > targetTurn)
                return;
            if (item.IsTurnEnd)
                return;
            await onDelta(item.Kind, item.MessageId, item.Ordinal, item.Text, ct);
        }
    }

    public Task<IReadOnlyList<AgentRuntimeForm>> ListPendingFormsAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgentRuntimeForm>>([]);

    public Task<bool> ReplyFormAsync(string sessionId, string formId, string answerJson, CancellationToken ct) =>
        Task.FromException<bool>(new InvalidOperationException("Interactive forms are not supported by the native runtime yet."));

    public Task SwitchAgentAsync(string sessionId, string agent, CancellationToken ct) =>
        Task.FromException(new InvalidOperationException("Agent switching is not supported by the native runtime yet."));

    public Task<int?> ReadContextLimitAsync(string? modelId, CancellationToken ct)
    {
        var limit = _sessions.Values
            .Where(s => string.IsNullOrWhiteSpace(modelId) || string.Equals(s.LastModel, modelId, StringComparison.Ordinal))
            .Select(s => s.ContextLimit)
            .FirstOrDefault(value => value is > 0);
        return Task.FromResult(limit);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        var states = _sessions.Values.ToArray();
        var tasks = new List<Task>();
        foreach (var session in states)
        {
            lock (session.Sync)
            {
                session.ActiveTurn?.Cancel();
                if (session.ActiveTask is not null)
                    tasks.Add(session.ActiveTask);
            }
        }
        try { await Task.WhenAll(tasks); } catch { }
        foreach (var session in states)
        {
            lock (session.Sync)
            {
                session.ActiveTurn?.Dispose();
                session.ActiveTurn = null;
            }
            session.Deltas.Writer.TryComplete();
        }
    }

    private async Task RunTurnAsync(
        SessionState session,
        int turnNumber,
        string model,
        AgentPromptControls? controls,
        CancellationToken ct)
    {
        var messageId = $"native:{session.Id}:{turnNumber}";
        var generationStarted = Stopwatch.GetTimestamp();
        var firstTokenAt = 0L;
        var finish = "stop";
        string? error = null;
        var emittedContent = false;
        var responseText = new StringBuilder();
        ResolvedProvider? resolved = null;

        try
        {
            resolved = await ResolveProviderAsync(model, ct);
            if (resolved.IsLocal && _llama.UseOwnSwapper)
                await _llamaSync.RunLockedAsync(
                    () => _llama.EnsureSwapModelAsync(resolved.Model, ct),
                    ct);

            using var providerHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var provider = string.Equals(resolved.ProviderId, "anthropic", StringComparison.OrdinalIgnoreCase)
                ? new AnthropicProvider(providerHttp, resolved.BaseUrl, resolved.ApiKey)
                : _providerFactory(providerHttp, resolved.BaseUrl, resolved.ApiKey);
            await using var mcpClient = controls?.HarnessAuthorization is { } harnessAuthorization
                ? await _mcpClientFactory(harnessAuthorization, ct)
                : null;
            var mcpTools = new JsonArray();
            if (mcpClient is not null)
            {
                foreach (var tool in mcpClient.OpenAiTools)
                    mcpTools.Add(tool?.DeepClone());
            }
            var commandPrefixes = _loadSettings().AllowedCommandPrefixes
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (controls?.HarnessAuthorization is { } commandAuth
                && commandPrefixes.Length > 0
                && IsRunCommandAuthorized(commandAuth))
                mcpTools.Add(NativeCommandRunner.ToolDefinition());

            var hasFinalText = false;
            string? lastToolCallFingerprint = null;
            var consecutiveToolCallFingerprintCount = 0;
            var agentStep = 0;
            var nextToolOrdinal = 0;
            for (; agentStep < MaxAgentSteps; agentStep++)
            {
                ct.ThrowIfCancellationRequested();
                var messages = await BuildContextMessagesAsync(session, provider, resolved.Model, controls, ct);
                var request = new ChatProviderRequest(
                    resolved.Model,
                    messages,
                    (JsonArray)mcpTools.DeepClone(),
                    controls?.Temperature,
                    controls?.ReasoningEffort,
                    controls?.MaxTokens);

                var stepText = new StringBuilder();
                var toolCalls = new SortedDictionary<int, ToolCallAccumulator>();
                var finishReason = "stop";
                var requestCompleted = false;

                for (var attempt = 1; attempt <= MaxAttempts && !requestCompleted; attempt++)
                {
                    var stepEmittedContent = false;
                    try
                    {
                        await foreach (var item in provider.StreamAsync(request, ct))
                        {
                            ct.ThrowIfCancellationRequested();
                            switch (item.Kind)
                            {
                                case ChatProviderEventKind.TextDelta when !string.IsNullOrEmpty(item.Text):
                                    firstTokenAt = firstTokenAt == 0 ? Stopwatch.GetTimestamp() : firstTokenAt;
                                    stepEmittedContent = true;
                                    emittedContent = true;
                                    stepText.Append(item.Text);
                                    responseText.Append(item.Text);
                                    await AppendDeltaAsync(session, turnNumber, messageId, "text", item.Text, ct);
                                    break;

                                case ChatProviderEventKind.ReasoningDelta when !string.IsNullOrEmpty(item.Text):
                                    firstTokenAt = firstTokenAt == 0 ? Stopwatch.GetTimestamp() : firstTokenAt;
                                    stepEmittedContent = true;
                                    emittedContent = true;
                                    await AppendDeltaAsync(session, turnNumber, messageId, "reasoning", item.Text, ct);
                                    break;

                                case ChatProviderEventKind.Usage:
                                    lock (session.Sync)
                                        session.Usage = session.Usage with
                                        {
                                            PromptTokens = session.Usage.PromptTokens + (item.PromptTokens ?? 0),
                                            CompletionTokens = session.Usage.CompletionTokens + (item.CompletionTokens ?? 0)
                                        };
                                    break;

                                case ChatProviderEventKind.Finished when item.FinishReason is not null and not "done":
                                    finishReason = item.FinishReason;
                                    break;

                                case ChatProviderEventKind.ToolCallDelta:
                                    stepEmittedContent = true;
                                    emittedContent = true;
                                    var index = item.ToolIndex ?? 0;
                                    if (!toolCalls.TryGetValue(index, out var accumulator))
                                    {
                                        accumulator = new ToolCallAccumulator(index);
                                        toolCalls.Add(index, accumulator);
                                    }
                                    accumulator.Append(item.ToolCallId, item.ToolName, item.ToolArguments);
                                    break;
                            }
                        }
                        requestCompleted = true;
                    }
                    catch (Exception ex) when (IsRetryable(ex) && attempt < MaxAttempts
                        && !stepEmittedContent && !ct.IsCancellationRequested)
                    {
                        _log?.Invoke($"native provider request failed transiently (attempt {attempt}/{MaxAttempts}); retrying");
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
                    }
                }

                if (!requestCompleted)
                    throw new HttpRequestException("Chat provider did not complete the request.");

                if (toolCalls.Count > 0)
                {
                    if (!string.Equals(finishReason, "tool_calls", StringComparison.Ordinal))
                    {
                        finish = "error";
                        error = "The model returned an incomplete tool call; it was not executed.";
                        break;
                    }

                    var toolCallFingerprint = CreateToolCallFingerprint(toolCalls.Values);
                    if (string.Equals(toolCallFingerprint, lastToolCallFingerprint, StringComparison.Ordinal))
                        consecutiveToolCallFingerprintCount++;
                    else
                    {
                        lastToolCallFingerprint = toolCallFingerprint;
                        consecutiveToolCallFingerprintCount = 1;
                    }

                    if (consecutiveToolCallFingerprintCount >= 3)
                    {
                        finish = "error";
                        error = "The agent repeated the same tool call with identical arguments three times; stopped to prevent a tool loop.";
                        break;
                    }

                    var repeatedToolCall = consecutiveToolCallFingerprintCount == 2;
                    var toolCallMessages = new JsonArray();
                    foreach (var pair in toolCalls)
                    {
                        var call = pair.Value;
                        // Missing IDs/names and malformed arguments are never executed. A synthetic
                        // ID is used only to return a protocol-valid error message to the model.
                        call.EffectiveId = string.IsNullOrWhiteSpace(call.Id)
                            ? $"beacon-invalid-{turnNumber}-{call.Index}"
                            : call.Id;
                        call.EffectiveName = string.IsNullOrWhiteSpace(call.Name) ? "unknown_tool" : call.Name;
                        toolCallMessages.Add(new JsonObject
                        {
                            ["id"] = call.EffectiveId,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = call.EffectiveName,
                                ["arguments"] = call.Arguments.ToString()
                            }
                        });
                    }

                    var assistantToolMessage = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = stepText.Length == 0 ? null : JsonValue.Create(stepText.ToString()),
                        ["tool_calls"] = toolCallMessages
                    };
                    lock (session.Sync)
                        session.Messages.Add(assistantToolMessage);
                    await PersistAsync(session, ct);

                    var executedTools = await Task.WhenAll(toolCalls.Values.Select(async call =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var startedAt = Stopwatch.GetTimestamp();
                        var isError = false;
                        string toolResult;
                        try
                        {
                            if (repeatedToolCall)
                            {
                                isError = true;
                                toolResult = "Tool error: this exact tool call with identical arguments was already repeated. Do not repeat it again; choose a different action or provide a final answer.";
                            }
                            else if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name))
                            {
                                isError = true;
                                toolResult = "Tool error: the model returned a tool call without a valid id or name.";
                            }
                            else if (!call.TryReadArguments(out var parsedArguments))
                            {
                                isError = true;
                                toolResult = "Tool error: the model returned malformed JSON arguments. Return a valid JSON object and try again.";
                            }
                            else if (string.Equals(call.Name, "run_command", StringComparison.Ordinal))
                            {
                                if (controls?.HarnessAuthorization is not { } commandAuthorization
                                    || commandPrefixes.Length == 0
                                    || !IsRunCommandAuthorized(commandAuthorization))
                                {
                                    isError = true;
                                    toolResult = "Tool error: run_command is disabled because the command allowlist or pipeline subtask permission is missing.";
                                }
                                else
                                {
                                    var result = await NativeCommandRunner.ExecuteAsync(
                                        commandAuthorization.WorkingDirectory,
                                        parsedArguments!,
                                        commandPrefixes,
                                        ct);
                                    isError = result.IsError;
                                    toolResult = result.IsError ? "Tool error: " + result.Text : result.Text;
                                }
                            }
                            else if (mcpClient is null)
                            {
                                isError = true;
                                toolResult = "Tool error: no scoped MCP authorization is available for this command.";
                            }
                            else if (!IsAdvertisedMcpTool(mcpClient.OpenAiTools, call.Name!))
                            {
                                isError = true;
                                toolResult = $"Tool error: MCP tool '{call.Name}' was not advertised by the scoped server and cannot be executed.";
                            }
                            else
                            {
                                var result = await mcpClient.CallToolAsync(call.Name!, parsedArguments!, ct);
                                isError = result.IsError;
                                toolResult = result.IsError
                                    ? "Tool error: " + result.Text
                                    : result.Text;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            isError = true;
                            toolResult = "Tool error: " + SafeError(ex);
                        }

                        var durationMs = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                        return (Call: call, Result: TruncateToolResult(toolResult), IsError: isError, DurationMs: durationMs);
                    }));

                    // Execute calls concurrently, but append their results in the model's index
                    // order so the transcript remains deterministic and every tool_call_id matches.
                    foreach (var executed in executedTools.OrderBy(item => item.Call.Index))
                    {
                        var call = executed.Call;
                        var toolResult = executed.Result;
                        lock (session.Sync)
                        {
                            session.Messages.Add(new JsonObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = call.EffectiveId,
                                ["content"] = toolResult
                            });
                        }
                        await AppendToolPartAsync(
                            session,
                            turnNumber,
                            messageId,
                            nextToolOrdinal++,
                            call.Name ?? "unknown_tool",
                            call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString(),
                            toolResult,
                            executed.IsError,
                            executed.DurationMs,
                            ct);
                    }

                    finish = "tool_calls";
                    continue;
                }

                if (string.Equals(finishReason, "tool_calls", StringComparison.Ordinal))
                {
                    finish = "error";
                    error = "The model requested tool execution but did not provide a complete tool call.";
                    break;
                }

                if (stepText.Length > 0)
                {
                    lock (session.Sync)
                        session.Messages.Add(new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = stepText.ToString()
                        });
                    await PersistAsync(session, ct);
                    hasFinalText = true;
                }

                finish = finishReason;
                break;
            }

            if (agentStep >= MaxAgentSteps && !hasFinalText && error is null)
            {
                finish = "error";
                error = $"The agent reached the maximum of {MaxAgentSteps} model/tool steps without a final answer.";
            }
            else if (agentStep >= MaxAgentSteps && error is null)
            {
                finish = "error";
                error = $"The agent reached the maximum of {MaxAgentSteps} model/tool steps.";
            }

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            if (error is not null)
            {
                await CompleteAssistantPartsAsync(session, messageId, ct);
                await AppendErrorAsync(session, turnNumber, messageId, error, ct);
            }
            else if (!hasFinalText)
            {
                error = emittedContent
                    ? "The model completed the turn without a final text answer."
                    : "The model returned no text.";
                finish = "error";
                await CompleteAssistantPartsAsync(session, messageId, ct);
                await AppendErrorAsync(session, turnNumber, messageId, error, ct);
            }
            else
            {
                await CompleteAssistantPartsAsync(session, messageId, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            finish = "aborted";
            error = null;
            await CompleteAssistantPartsAsync(session, messageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            finish = "error";
            error = SafeError(ex);
            try { await AppendErrorAsync(session, turnNumber, messageId, error, CancellationToken.None); } catch { }
            _log?.Invoke($"native agent turn failed: {error}");
        }
        finally
        {
            var elapsed = firstTokenAt == 0 ? 0 : Stopwatch.GetElapsedTime(firstTokenAt).TotalSeconds;
            lock (session.Sync)
            {
                session.Usage = session.Usage with
                {
                    AssistantMessages = session.Usage.AssistantMessages + 1,
                    GenerationSeconds = session.Usage.GenerationSeconds + elapsed
                };
                session.TurnStatus = new AgentTurnStatus(true, finish, error, messageId);
                if (session.ActiveTurn is { } active && active.Token == ct)
                {
                    active.Dispose();
                    session.ActiveTurn = null;
                }
            }
            try { await PersistAsync(session, CancellationToken.None); } catch { }
            session.Deltas.Writer.TryWrite(NativeDelta.End(turnNumber));
        }
    }

    private async Task AppendDeltaAsync(
        SessionState session,
        int turnNumber,
        string messageId,
        string kind,
        string delta,
        CancellationToken ct)
    {
        var ordinal = 0;
        var externalId = $"{messageId}:{kind}:{ordinal}";
        lock (session.Sync)
        {
            var index = session.Parts.FindIndex(p => string.Equals(p.ExternalId, externalId, StringComparison.Ordinal));
            if (index < 0)
                session.Parts.Add(new AgentMessagePart("assistant", kind, delta, externalId, Complete: false));
            else
                session.Parts[index] = session.Parts[index] with { Body = session.Parts[index].Body + delta, Complete = false };
        }
        await PersistAsync(session, ct);
        session.Deltas.Writer.TryWrite(new NativeDelta(turnNumber, kind, messageId, ordinal, delta, false));
    }

    private async Task AppendToolPartAsync(
        SessionState session,
        int turnNumber,
        string messageId,
        int ordinal,
        string tool,
        string input,
        string result,
        bool isError,
        int durationMs,
        CancellationToken ct)
    {
        var body = $"{tool}: {result}";
        var externalId = $"{messageId}:tool:{ordinal}";
        var part = new AgentMessagePart(
            "assistant",
            "tool",
            body,
            externalId,
            Complete: true,
            Tool: tool,
            ToolStatus: isError ? "error" : "completed",
            ToolInput: input,
            ToolResult: result,
            DurationMs: durationMs);
        lock (session.Sync)
        {
            var existing = session.Parts.FindIndex(item => string.Equals(item.ExternalId, externalId, StringComparison.Ordinal));
            if (existing < 0)
                session.Parts.Add(part);
            else
                session.Parts[existing] = part;
        }

        await PersistAsync(session, ct);
        session.Deltas.Writer.TryWrite(new NativeDelta(turnNumber, "tool", messageId, ordinal, body, false));
    }

    private async Task CompleteAssistantPartsAsync(SessionState session, string messageId, CancellationToken ct)
    {
        lock (session.Sync)
        {
            for (var i = 0; i < session.Parts.Count; i++)
            {
                var part = session.Parts[i];
                if (part.Role == "assistant"
                    && part.ExternalId is not null
                    && part.ExternalId.StartsWith(messageId + ":", StringComparison.Ordinal))
                    session.Parts[i] = part with { Complete = true };
            }
        }
        await PersistAsync(session, ct);
    }

    private async Task AppendErrorAsync(SessionState session, int turnNumber, string messageId, string message, CancellationToken ct)
    {
        lock (session.Sync)
        {
            var id = $"native-error:{session.Id}:{turnNumber}";
            if (session.Parts.All(p => p.ExternalId != id))
                session.Parts.Add(new AgentMessagePart("assistant", "error", message, id));
            session.TurnStatus = new AgentTurnStatus(false, "error", message, messageId);
        }
        await PersistAsync(session, ct);
    }

    private async Task<JsonArray> BuildContextMessagesAsync(
        SessionState session,
        IChatProvider provider,
        string model,
        AgentPromptControls? controls,
        CancellationToken ct)
    {
        List<JsonObject> snapshot;
        lock (session.Sync)
            snapshot = session.Messages.Select(m => (JsonObject)m.DeepClone()).ToList();

        var limit = controls?.ContextLimit is > 0 ? controls.ContextLimit.Value : session.ContextLimit.GetValueOrDefault();
        if (limit > 0 && EstimateTokens(snapshot) > (int)(limit * 0.82) && snapshot.Count > 3)
        {
            var preserve = Math.Min(6, snapshot.Count - 1);
            var splitAt = FindSafeContextSplitIndex(snapshot, snapshot.Count - preserve);
            var keep = snapshot.Skip(splitAt).ToList();
            var old = snapshot.Skip(1).Take(splitAt - 1).ToList();
            if (old.Count > 0)
            {
                var summaryRequest = new ChatProviderRequest(
                    model,
                    new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "system",
                            ["content"] = "Summarize the conversation history for continuation. Preserve the user's objective, constraints, decisions, important identifiers, completed work, unresolved questions, and exact errors. Be concise and do not invent facts."
                        },
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonArray(old.Select(m => (JsonNode?)m.DeepClone()).ToArray()).ToJsonString()
                        }
                    },
                    new JsonArray(),
                    controls?.Temperature,
                    controls?.ReasoningEffort,
                    controls?.MaxTokens is > 0 ? Math.Min(controls.MaxTokens.Value, 2048) : 2048);
                var summary = new StringBuilder();
                await foreach (var item in provider.StreamAsync(summaryRequest, ct))
                    if (item.Kind == ChatProviderEventKind.TextDelta && item.Text is not null)
                        summary.Append(item.Text);

                if (summary.Length == 0)
                    throw new InvalidOperationException(
                        "The conversation exceeded its context budget, but the model returned an empty summary. The existing history was preserved.");

                var compacted = new List<JsonObject> { snapshot[0] };
                compacted.Add(new JsonObject { ["role"] = "system", ["content"] = "Conversation summary (preserve this as context):\\n" + summary.ToString() });
                compacted.AddRange(keep);
                snapshot = compacted;
                lock (session.Sync)
                {
                    session.Messages.Clear();
                    session.Messages.AddRange(snapshot.Select(m => (JsonObject)m.DeepClone()));
                }
                await PersistAsync(session, ct);
            }
        }

        var result = new JsonArray();
        foreach (var message in snapshot)
            result.Add(message.DeepClone());
        return result;
    }

    internal static int FindSafeContextSplitIndex(IReadOnlyList<JsonObject> messages, int requestedIndex)
    {
        if (messages.Count <= 1)
            return messages.Count;

        var splitAt = Math.Clamp(requestedIndex, 1, messages.Count);
        for (var i = 1; i < messages.Count; i++)
        {
            var message = messages[i];
            if (!string.Equals(message["role"]?.GetValue<string>(), "assistant", StringComparison.Ordinal)
                || message["tool_calls"] is not JsonArray calls
                || calls.Count == 0)
                continue;

            var pendingIds = calls
                .Select(call => call?["id"]?.GetValue<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            var groupEnd = i + 1;
            while (groupEnd < messages.Count
                && string.Equals(messages[groupEnd]["role"]?.GetValue<string>(), "tool", StringComparison.Ordinal))
            {
                var toolCallId = messages[groupEnd]["tool_call_id"]?.GetValue<string>();
                if (toolCallId is null || !pendingIds.Remove(toolCallId))
                    break;
                groupEnd++;
            }

            // Keep the assistant tool-call message and all matching results together. If a crash
            // left the group incomplete, retain the whole unfinished group rather than sending
            // an invalid orphaned tool call to the provider after compaction.
            if (pendingIds.Count > 0 && splitAt > i && splitAt < groupEnd)
                splitAt = i;
            else if (pendingIds.Count > 0 && splitAt == i + 1)
                splitAt = i;
            else if (pendingIds.Count == 0 && splitAt > i && splitAt < groupEnd)
                splitAt = groupEnd;
        }

        return Math.Clamp(splitAt, 1, messages.Count);
    }

    private static int EstimateTokens(IReadOnlyList<JsonObject> messages) =>
        Math.Max(1, Encoding.UTF8.GetByteCount(string.Join("\n", messages.Select(m => m.ToJsonString()))) / 4);

    private static bool IsAdvertisedMcpTool(JsonArray tools, string name)
    {
        foreach (var tool in tools)
        {
            if (string.Equals(
                tool?["function"]?["name"]?.GetValue<string>(),
                name,
                StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string CreateToolCallFingerprint(IEnumerable<ToolCallAccumulator> calls)
    {
        var signature = string.Join("\n", calls
            .OrderBy(call => call.Index)
            .Select(call => $"{call.Name ?? "unknown_tool"}\0{call.Arguments}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
    }

    internal static bool IsRunCommandAuthorized(AgentHarnessAuthorization authorization)
    {
        // Legacy chat sessions are governed by workstation-local AllowedCommandPrefixes.
        // Pipeline commands additionally require an explicit subtask grant and Actor role.
        if (string.IsNullOrWhiteSpace(authorization.PipelineRole))
            return true;
        if (!string.Equals(authorization.PipelineRole, "Actor", StringComparison.OrdinalIgnoreCase))
            return false;
        return authorization.AllowedMcpTools?.Any(tool =>
            string.Equals(tool, "run_command", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, "beacon_run_command", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private async Task<ResolvedProvider> ResolveProviderAsync(string model, CancellationToken ct)
    {
        var separator = model.IndexOf('/');
        if (separator <= 0 || separator == model.Length - 1)
            throw new InvalidOperationException("Model id must use provider/model format.");
        var providerId = model[..separator].Trim();
        var modelId = model[(separator + 1)..].Trim();
        if (providerId.Equals("beacon-local", StringComparison.OrdinalIgnoreCase))
        {
            var port = _llama.Port > 0 ? _llama.Port : _loadSettings().LlamaSwapPort;
            return new ResolvedProvider("beacon-local", $"http://127.0.0.1:{port}/v1", null, modelId, true);
        }

        using var response = await _controlPlane.GetAsync("/v1/devices/me/opencode-connections", ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Could not load provider connections (HTTP {(int)response.StatusCode}).");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var wrapped))
            root = wrapped;
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Provider connection response is not a list.");

        foreach (var row in root.EnumerateArray())
        {
            var id = ReadString(row, "providerId");
            if (!string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase))
                continue;
            var baseUrl = ReadString(row, "baseUrl");
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException($"Provider '{providerId}' has no base URL.");
            var apiKey = ReadString(row, "apiKey");
            return new ResolvedProvider(providerId, baseUrl.TrimEnd('/'), apiKey, modelId, false);
        }

        throw new InvalidOperationException($"No connection is configured for provider '{providerId}'.");
    }

    private async Task<SessionState> GetSessionAsync(string id, CancellationToken ct)
    {
        var normalizedId = NormalizeSessionId(id);
        if (_sessions.TryGetValue(normalizedId, out var existing))
            return existing;

        var path = TranscriptPath(_loadSettings(), Guid.Empty, normalizedId);
        if (!File.Exists(path))
            throw new InvalidOperationException("Agent session was not found.");
        var text = await File.ReadAllTextAsync(path, ct);
        var saved = JsonSerializer.Deserialize<PersistedSession>(text, JsonOptions)
            ?? throw new InvalidDataException("Stored agent transcript is invalid.");
        if (!string.Equals(saved.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Stored agent transcript does not match the requested session.");

        var state = new SessionState
        {
            Id = normalizedId,
            Title = saved.Title,
            CreatedAt = saved.CreatedAt,
            TranscriptPath = path,
            Messages = saved.Messages,
            Parts = saved.Parts,
            TurnNumber = saved.TurnNumber,
            LastModel = saved.LastModel,
            ContextLimit = saved.ContextLimit,
            Usage = saved.Usage,
            TurnStatus = saved.TurnStatus
        };
        var changed = false;
        lock (state.Sync)
        {
            if (state.TurnStatus is { Ended: false } interrupted)
            {
                state.TurnStatus = new AgentTurnStatus(true, "aborted", "Client restarted during the previous turn.", interrupted.MessageId);
                state.Parts = state.Parts.Select(p => p.Complete ? p : p with { Complete = true }).ToList();
                changed = true;
            }
            changed |= RecoverInterruptedToolCalls(state);
        }

        if (!_sessions.TryAdd(normalizedId, state))
            return _sessions[normalizedId];

        if (changed)
        {
            try
            {
                await PersistAsync(state, ct);
            }
            catch
            {
                _sessions.TryRemove(normalizedId, out _);
                throw;
            }
        }

        return state;
    }

    private static string NormalizeSessionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || !Guid.TryParseExact(id, "N", out var parsed)
            || parsed == Guid.Empty)
            throw new InvalidOperationException("Agent session id is invalid.");

        return parsed.ToString("N");
    }

    /// <summary>
    /// Repairs interrupted tool-call batches without replaying operations whose execution result
    /// was not durably recorded. A tool may have completed just before a workstation crash, so the
    /// recovery result explicitly tells the model to inspect state before attempting the action again.
    /// Must be called while holding the session lock.
    /// </summary>
    private static bool RecoverInterruptedToolCalls(SessionState session)
    {
        var changed = false;
        for (var i = 0; i < session.Messages.Count; i++)
        {
            var assistant = session.Messages[i];
            if (!string.Equals(assistant["role"]?.GetValue<string>(), "assistant", StringComparison.Ordinal)
                || assistant["tool_calls"] is not JsonArray calls
                || calls.Count == 0)
                continue;

            var expected = new List<(string Id, string Name)>();
            foreach (var call in calls)
            {
                var id = call?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var name = call?["function"]?["name"]?.GetValue<string>();
                expected.Add((id, string.IsNullOrWhiteSpace(name) ? "unknown_tool" : name));
            }
            if (expected.Count == 0)
                continue;

            var recorded = new HashSet<string>(StringComparer.Ordinal);
            var insertAt = i + 1;
            while (insertAt < session.Messages.Count
                && string.Equals(session.Messages[insertAt]["role"]?.GetValue<string>(), "tool", StringComparison.Ordinal))
            {
                var resultId = session.Messages[insertAt]["tool_call_id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(resultId))
                    recorded.Add(resultId);
                insertAt++;
            }

            foreach (var call in expected)
            {
                if (!recorded.Add(call.Id))
                    continue;

                const string recoveryMessage =
                    "Tool execution was interrupted before its result was durably recorded. The operation may or may not have completed. " +
                    "It was not replayed automatically to avoid duplicating side effects. Inspect the current state before retrying.";

                session.Messages.Insert(insertAt++, new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["content"] = recoveryMessage
                });
                session.Parts.Add(new AgentMessagePart(
                    "assistant",
                    "tool",
                    $"{call.Name}: {recoveryMessage}",
                    $"native-recovery:{session.Id}:{call.Id}",
                    Complete: true,
                    Tool: call.Name,
                    ToolStatus: "error",
                    ToolResult: recoveryMessage));
                changed = true;
            }

            i = insertAt - 1;
        }

        return changed;
    }

    private static string TranscriptPath(WorkstationSettings settings, Guid _, string? id)
    {
        var parent = string.IsNullOrWhiteSpace(settings.OpencodeDataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectBeacon")
            : Path.GetDirectoryName(Path.GetFullPath(settings.OpencodeDataDir)) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(parent, "sessions", (id ?? Guid.NewGuid().ToString("N")) + ".json");
    }

    private async Task PersistAsync(SessionState session, CancellationToken ct)
    {
        PersistedSession snapshot;
        await session.Persistence.WaitAsync(ct);
        try
        {
            lock (session.Sync)
            {
                snapshot = new PersistedSession
                {
                    Id = session.Id,
                    Title = session.Title,
                    CreatedAt = session.CreatedAt,
                    Messages = session.Messages.Select(m => (JsonObject)m.DeepClone()).ToList(),
                    Parts = session.Parts.ToList(),
                    TurnNumber = session.TurnNumber,
                    LastModel = session.LastModel,
                    ContextLimit = session.ContextLimit,
                    Usage = session.Usage,
                    TurnStatus = session.TurnStatus
                };
            }
            var dir = Path.GetDirectoryName(session.TranscriptPath)!;
            Directory.CreateDirectory(dir);
            var temp = session.TranscriptPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot, JsonOptions), ct);
                File.Move(temp, session.TranscriptPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }
        finally
        {
            session.Persistence.Release();
        }
    }

    private static string TruncateToolResult(string result)
    {
        if (result.Length <= MaxToolResultCharacters)
            return result;

        var keptCharacters = MaxToolResultCharacters;
        string marker;
        do
        {
            marker = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "\n\n[Tool result truncated: original length {0} characters; showing first {1} characters.]",
                result.Length,
                keptCharacters);
            keptCharacters = MaxToolResultCharacters - marker.Length;
        }
        while (marker.Length + keptCharacters > MaxToolResultCharacters);

        marker = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "\n\n[Tool result truncated: original length {0} characters; showing first {1} characters.]",
            result.Length,
            keptCharacters);
        return result[..keptCharacters] + marker;
    }

    private static bool IsRetryable(Exception ex) =>
        ex is ChatProviderException provider
            ? provider.Retryable
            : ex is HttpRequestException or IOException;

    private static string SafeError(Exception ex) => ex switch
    {
        ChatProviderException provider => provider.Message,
        HttpRequestException => "The chat provider connection failed.",
        OperationCanceledException => "The generation was cancelled.",
        InvalidDataException => ex.Message,
        InvalidOperationException => ex.Message,
        _ => "The native agent runtime encountered an unexpected error."
    };

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NativeAgentRuntime));
    }

    private sealed class ToolCallAccumulator(int index)
    {
        public int Index { get; } = index;
        public string? Id { get; private set; }
        public string? Name { get; private set; }
        public StringBuilder Arguments { get; } = new();
        public string? EffectiveId { get; set; }
        public string? EffectiveName { get; set; }

        public void Append(string? id, string? name, string? arguments)
        {
            if (!string.IsNullOrEmpty(id))
                Id = (Id ?? "") + id;
            if (!string.IsNullOrEmpty(name))
                Name = (Name ?? "") + name;
            if (!string.IsNullOrEmpty(arguments))
                Arguments.Append(arguments);
        }

        public bool TryReadArguments(out JsonObject? parsed)
        {
            parsed = null;
            try
            {
                var raw = Arguments.Length == 0 ? "{}" : Arguments.ToString();
                parsed = JsonNode.Parse(raw) as JsonObject;
                return parsed is not null;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    private sealed record ResolvedProvider(string ProviderId, string BaseUrl, string? ApiKey, string Model, bool IsLocal);

    private sealed record NativeDelta(int TurnNumber, string Kind, string MessageId, int Ordinal, string Text, bool IsTurnEnd)
    {
        public static NativeDelta End(int turn) => new(turn, "", "", 0, "", true);
    }

    private sealed class SessionState
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Persistence { get; } = new(1, 1);
        public Channel<NativeDelta> Deltas { get; } = Channel.CreateUnbounded<NativeDelta>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string TranscriptPath { get; set; } = "";
        public List<JsonObject> Messages { get; set; } = [];
        public List<AgentMessagePart> Parts { get; set; } = [];
        public int TurnNumber { get; set; }
        public string? LastModel { get; set; }
        public int? ContextLimit { get; set; }
        public AgentSessionUsage Usage { get; set; } = new(0, 0, 0, 0);
        public AgentTurnStatus? TurnStatus { get; set; }
        public CancellationTokenSource? ActiveTurn { get; set; }
        public Task? ActiveTask { get; set; }
    }

    private sealed class PersistedSession
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public List<JsonObject> Messages { get; set; } = [];
        public List<AgentMessagePart> Parts { get; set; } = [];
        public int TurnNumber { get; set; }
        public string? LastModel { get; set; }
        public int? ContextLimit { get; set; }
        public AgentSessionUsage Usage { get; set; } = new(0, 0, 0, 0);
        public AgentTurnStatus? TurnStatus { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
