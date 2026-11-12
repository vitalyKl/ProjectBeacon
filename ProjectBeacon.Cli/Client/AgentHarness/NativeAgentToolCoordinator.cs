namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Coordinates a complete batch of model-emitted tool calls. It preserves the runtime's concurrency
/// contract: ordinary tools run in parallel, user-input tools run only after siblings finish, while
/// transcript and tool-part writes remain in model index order.
/// </summary>
internal sealed class NativeAgentToolCoordinator(
    NativeAgentSessionStore sessionStore,
    NativeAgentToolExecutor toolExecutor,
    NativeAgentTurnFinalizer turnFinalizer)
{
    private readonly NativeAgentSessionStore _sessionStore = sessionStore;
    private readonly NativeAgentToolExecutor _toolExecutor = toolExecutor;
    private readonly NativeAgentTurnFinalizer _turnFinalizer = turnFinalizer;

    public async Task<NativeAgentToolBatchResult> ExecuteBatchAsync(
        NativeAgentSessionState session,
        int turnNumber,
        string messageId,
        NativeAgentModelStepResult step,
        string agentMode,
        AgentPromptControls? controls,
        IMcpToolClient? mcpClient,
        NativeAgentToolCatalogResult toolCatalog,
        IModelTaskLease taskLease,
        NativeAgentToolLoopState loopState,
        CancellationToken ct)
    {
        if (step.ToolCalls.Count == 0)
            return new NativeAgentToolBatchResult(
                false,
                string.Equals(step.FinishReason, "tool_calls", StringComparison.Ordinal)
                    ? "The model requested tool execution but did not provide a complete tool call."
                    : null);

        if (!string.Equals(step.FinishReason, "tool_calls", StringComparison.Ordinal))
            return new NativeAgentToolBatchResult(
                false,
                "The model returned an incomplete tool call; it was not executed.");

        ct.ThrowIfCancellationRequested();
        var fingerprint = NativeAgentToolPolicy.CreateToolCallFingerprint(step.ToolCalls);
        if (string.Equals(fingerprint, loopState.LastToolCallFingerprint, StringComparison.Ordinal))
            loopState.ConsecutiveToolCallFingerprintCount++;
        else
        {
            loopState.LastToolCallFingerprint = fingerprint;
            loopState.ConsecutiveToolCallFingerprintCount = 1;
        }

        // A repeated batch is not itself terminal: the model receives warnings and can choose a
        // different action. Successful identical read-only calls may still use the turn cache.
        var repeatedToolCall = loopState.ConsecutiveToolCallFingerprintCount >= 2;
        var toolCallMessages = new JsonArray();
        foreach (var call in step.ToolCalls.OrderBy(item => item.Index))
        {
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
            ["content"] = string.IsNullOrEmpty(step.Text) ? null : JsonValue.Create(step.Text),
            ["tool_calls"] = toolCallMessages
        };
        lock (session.Sync)
            session.Messages.Add(assistantToolMessage);
        await _sessionStore.PersistAsync(session, ct);

        var executionContext = new NativeToolExecutionContext(
            session,
            agentMode,
            controls,
            mcpClient,
            toolCatalog.Tools,
            repeatedToolCall,
            loopState.ResultCache,
            taskLease,
            toolCatalog.ManifestFingerprint,
            toolCatalog.ManifestTrustedAtTurnStart);

        // Approval forms may pause the task lease. They are deliberately run after all parallel
        // siblings complete, so a paused session never leaves concurrent work using its worktree.
        var parallelCalls = step.ToolCalls
            .Where(call => !NativeAgentToolPolicy.MayRequestUserInput(call.Name))
            .Select(call => _toolExecutor.ExecuteAsync(executionContext, call, ct))
            .ToArray();
        var approvalCalls = step.ToolCalls
            .Where(call => NativeAgentToolPolicy.MayRequestUserInput(call.Name))
            .OrderBy(call => call.Index)
            .ToArray();

        var executedTools = new List<NativeExecutedToolCall>();
        executedTools.AddRange(await Task.WhenAll(parallelCalls));
        foreach (var call in approvalCalls)
            executedTools.Add(await _toolExecutor.ExecuteAsync(executionContext, call, ct));

        // Any non-cacheable operation may have changed project state, even when it reports an error
        // after a partial write. Invalidate reads before a subsequent model step.
        if (executedTools.Any(item => !NativeAgentToolPolicy.IsCacheableTool(item.Call.Name)))
            loopState.ResultCache.Clear();

        // Execution may be concurrent, but append results in index order so every tool_call_id
        // remains aligned with the assistant tool-call message and the transcript is deterministic.
        foreach (var executed in executedTools.OrderBy(item => item.Call.Index))
        {
            var call = executed.Call;
            var toolResult = executed.Result;
            var input = call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString();
            var toolName = call.Name ?? "unknown_tool";

            lock (session.Sync)
            {
                session.Messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.EffectiveId,
                    ["content"] = toolResult
                });

                session.ToolJournal.Add(new NativeToolJournalEntry(
                    turnNumber,
                    toolName,
                    input,
                    toolResult,
                    executed.IsError,
                    executed.CacheHit,
                    DateTimeOffset.UtcNow));
                if (session.ToolJournal.Count > 200)
                    session.ToolJournal.RemoveRange(0, session.ToolJournal.Count - 200);
            }

            await _turnFinalizer.AppendToolPartAsync(
                session,
                turnNumber,
                messageId,
                loopState.NextToolOrdinal++,
                toolName,
                input,
                toolResult,
                executed.IsError,
                executed.DurationMs,
                ct);
        }

        var pipelineRole = controls?.HarnessAuthorization?.PipelineRole;
        var isTerminal = executedTools.Any(item => !item.IsError
            && NativeAgentToolPolicy.IsSuccessfulTerminalTool(
                pipelineRole,
                item.Call.Name,
                item.Call.Arguments.ToString()));
        if (isTerminal)
            await _sessionStore.PersistAsync(session, ct);

        return new NativeAgentToolBatchResult(isTerminal);
    }

}
