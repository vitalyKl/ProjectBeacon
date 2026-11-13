namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;

/// <summary>
/// Builds provider context and performs bounded conversation compaction. It owns no sessions,
/// providers, tools, or command execution; persistence is supplied by the session store.
/// </summary>
internal sealed class NativeAgentContextBuilder(
    Func<WorkstationSettings> loadSettings,
    Action<string>? log,
    Func<NativeAgentSessionState, CancellationToken, Task> persistAsync)
{
    private const string CheckpointHeader = "Continuation checkpoint (internal state; preserve and follow it):";
    private const double CompactionTriggerRatio = 0.82;
    private const double CompactionTargetRatio = 0.6;
    private const int ElidedToolResultThreshold = 2000;
    private const int SummaryInputItemCharacters = 2000;

    private readonly Func<WorkstationSettings> _loadSettings = loadSettings;
    private readonly Action<string>? _log = log;
    private readonly Func<NativeAgentSessionState, CancellationToken, Task> _persistAsync = persistAsync;

    public async Task<JsonArray> BuildMessagesAsync(
        NativeAgentSessionState session,
        string model,
        AgentPromptControls? controls,
        JsonArray tools,
        Func<ChatProviderRequest, CancellationToken, Task<string>> summarize,
        CancellationToken ct,
        bool forceCompaction = false)
    {
        List<JsonObject> snapshot;
        List<NativeToolJournalEntry> toolJournal;
        int lastPromptTokens;
        int lastPromptMessageCount;
        lock (session.Sync)
        {
            snapshot = session.Messages.Select(m => (JsonObject)m.DeepClone()).ToList();
            // Keep the durable journal complete, but pass only a bounded excerpt into the
            // compaction prompt so a large number of tool results cannot defeat compaction.
            toolJournal = session.ToolJournal.TakeLast(30)
                .Select(entry => entry with
                {
                    Result = entry.Result.Length <= 1200
                        ? entry.Result
                        : entry.Result[..1200] + " [journal excerpt truncated]"
                })
                .ToList();
            lastPromptTokens = session.LastPromptTokens;
            lastPromptMessageCount = session.LastPromptMessageCount;
        }

        var limit = controls?.ContextLimit is > 0 ? controls.ContextLimit.Value : session.ContextLimit.GetValueOrDefault();
        if (limit <= 0)
        {
            bool announce;
            lock (session.Sync)
            {
                announce = !session.ContextLimitWarningLogged;
                session.ContextLimitWarningLogged = true;
            }
            if (announce)
                _log?.Invoke($"native context compaction is off: no context limit is known for model '{model}'; set the context size of its model backend");
        }
        else
        {
            // The tool definitions travel with every request, so they count against the window too.
            var toolTokens = EstimateToolTokens(tools);
            var promptTokens = EstimatePromptTokens(snapshot, toolTokens, lastPromptTokens, lastPromptMessageCount);
            var configuredReserve = Math.Max(0, _loadSettings().ContextResponseReserveTokens);
            var promptBudget = CalculatePromptBudget(limit, configuredReserve);
            if ((forceCompaction || promptTokens > promptBudget)
                && snapshot.Count > (controls?.HarnessAuthorization?.PipelineRole is null ? 3 : 4))
            {
                var changed = false;
                var protectedPrefixCount = controls?.HarnessAuthorization?.PipelineRole is null ? 1 : 2;
                var preserve = Math.Min(6, snapshot.Count - protectedPrefixCount);
                var splitAt = Math.Max(protectedPrefixCount,
                    FindSafeContextSplitIndex(snapshot, snapshot.Count - preserve));
                var keep = snapshot.Skip(splitAt).ToList();
                var old = snapshot.Skip(protectedPrefixCount).Take(splitAt - protectedPrefixCount).ToList();

                // A checkpoint that is the only thing left to compress is already as small as it gets.
                // Summarizing it again on every step would cost a model call and free nothing.
                if (old.Any(m => !IsCheckpointMessage(m)))
                {
                    _log?.Invoke($"native context compaction: about {promptTokens} of {limit} tokens used (tools about {toolTokens}, response reserve {limit - promptBudget}); summarizing {old.Count} messages");
                    var summaryInput = PrepareSummaryInput(old, limit);
                    var summaryRequest = new ChatProviderRequest(
                        model,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["role"] = "system",
                                ["content"] = "Create a compact continuation checkpoint as a valid JSON object with keys: goal, constraints, completed, inProgress, remaining, decisions, importantIdentifiers, importantFiles, toolResults, unresolvedQuestions, nextAction. Preserve exact identifiers, paths, tool outcomes, errors, and user constraints. The original task is preserved verbatim outside this checkpoint; do not duplicate or paraphrase it in the checkpoint. Distinguish verified facts from assumptions. Do not invent facts. The checkpoint is internal runtime state, not a user-facing final answer."
                            },
                            new JsonObject
                            {
                                ["role"] = "user",
                                ["content"] = new JsonObject
                                {
                                    ["conversation"] = new JsonArray(summaryInput.Select(m => (JsonNode?)m.DeepClone()).ToArray()),
                                    ["toolJournal"] = JsonSerializer.SerializeToNode(toolJournal, NativeAgentRuntimeSerialization.JsonOptions)
                                }.ToJsonString()
                            }
                        },
                        new JsonArray(),
                        controls?.Temperature,
                        "low",
                        4096);
                    var summary = new StringBuilder();
                    try
                    {
                        summary.Append(await summarize(summaryRequest, ct));
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested
                        && ex is HttpRequestException or IOException or InvalidDataException)
                    {
                        // The summary request can fail precisely because the window is already
                        // exhausted. That must not end the user's task: fall back below.
                        summary.Clear();
                        _log?.Invoke($"native context summary request failed ({ex.GetType().Name})");
                    }

                    if (summary.Length == 0)
                    {
                        // A failed/empty model summary must not terminate the user's task. Preserve
                        // a deterministic structured checkpoint from the recent transcript and tool journal.
                        var fallbackJournal = toolJournal.TakeLast(8)
                            .Select(entry => entry with
                            {
                                Result = entry.Result.Length <= 400 ? entry.Result : entry.Result[..400] + " [fallback journal entry clipped]"
                            }).ToArray();
                        summary.Append(BuildFallbackCheckpoint(old, fallbackJournal));
                        _log?.Invoke("native context compaction used a deterministic fallback checkpoint");
                    }

                    var compacted = snapshot.Take(protectedPrefixCount).Select(message => (JsonObject)message.DeepClone()).ToList();
                    compacted.Add(new JsonObject { ["role"] = "system", ["content"] = CheckpointHeader + "\n" + summary.ToString() });
                    compacted.AddRange(keep);
                    snapshot = compacted;
                    changed = true;
                }

                // The retained tail can still be too large (parallel reads, big tool results). Older tool
                // results are replaced by a pointer so the next step does not immediately compact again.
                if (EstimatePromptTokens(snapshot, toolTokens, 0, 0) > (int)(limit * CompactionTargetRatio)
                    && ElideOlderToolResults(snapshot, ElidedToolResultThreshold) > 0)
                    changed = true;

                if (changed)
                {
                    lock (session.Sync)
                    {
                        session.Messages.Clear();
                        session.Messages.AddRange(snapshot.Select(m => (JsonObject)m.DeepClone()));
                        // The reported size described the transcript that no longer exists.
                        session.LastPromptTokens = 0;
                        session.LastPromptMessageCount = 0;
                    }
                    await _persistAsync(session, ct);
                    _log?.Invoke($"native context after compaction: about {EstimatePromptTokens(snapshot, toolTokens, 0, 0)} of {limit} tokens");
                }
            }
        }

        var result = new JsonArray();
        foreach (var message in snapshot)
            result.Add(message.DeepClone());
        return result;
    }

    internal static int EstimatePromptTokens(
        IReadOnlyList<JsonObject> messages,
        int toolTokens,
        int lastPromptTokens,
        int lastPromptMessageCount)
    {
        var estimate = EstimateTokens(messages) + toolTokens;
        if (lastPromptTokens <= 0 || lastPromptMessageCount <= 0 || lastPromptMessageCount > messages.Count)
            return estimate;

        // The provider's own count for the previous request is exact; only what was added since
        // is estimated. The larger of the two wins, so a provider that under-reports (cached input)
        // can never make the budget look roomier than the estimate.
        var addedSince = lastPromptMessageCount == messages.Count
            ? 0
            : EstimateTokens(messages.Skip(lastPromptMessageCount).ToList());
        return Math.Max(estimate, lastPromptTokens + addedSince);
    }

    internal static int CalculatePromptBudget(int contextLimit, int configuredReserveTokens)
    {
        if (contextLimit <= 0)
            return 0;

        var automaticReserve = (int)Math.Ceiling(contextLimit * 0.25);
        var requestedReserve = configuredReserveTokens > 0 ? configuredReserveTokens : automaticReserve;
        // Always leave at least half of a tiny context window for the prompt itself.
        var reserve = Math.Clamp(requestedReserve, 0, contextLimit / 2);
        var ratioBudget = (int)Math.Floor(contextLimit * CompactionTriggerRatio);
        return Math.Max(1, Math.Min(ratioBudget, contextLimit - reserve));
    }

    internal static int EstimateToolTokens(JsonArray tools) =>
        tools.Count == 0 ? 0 : Math.Max(1, Encoding.UTF8.GetByteCount(tools.ToJsonString()) / 4);

    internal static bool IsCheckpointMessage(JsonObject message) =>
        string.Equals(message["role"]?.GetValue<string>(), "system", StringComparison.Ordinal)
        && message["content"] is JsonValue content
        && content.TryGetValue<string>(out var text)
        && text.StartsWith(CheckpointHeader, StringComparison.Ordinal);

    /// <summary>
    /// Bounds what the summary request sends. The summary call must fit the same window it is
    /// relieving, so oversized entries are clipped and, as a last resort, the oldest ones dropped.
    /// Earlier checkpoints are never clipped: they are the densest history there is.
    /// </summary>
    internal static List<JsonObject> PrepareSummaryInput(IReadOnlyList<JsonObject> old, int limit)
    {
        var budget = Math.Max(2048, (int)(limit * 0.6));
        var items = old.Select(m => ClipMessageForSummary(m, SummaryInputItemCharacters)).ToList();
        if (EstimateTokens(items) > budget)
            items = old.Select(m => ClipMessageForSummary(m, 400)).ToList();
        while (items.Count > 1 && EstimateTokens(items) > budget)
        {
            var drop = items.FindIndex(m => !IsCheckpointMessage(m));
            if (drop < 0 || drop == items.Count - 1)
                break;
            items.RemoveAt(drop);
        }

        return items;
    }

    private static JsonObject ClipMessageForSummary(JsonObject message, int maxChars)
    {
        var clone = (JsonObject)message.DeepClone();
        if (IsCheckpointMessage(clone))
            return clone;

        if (clone["content"] is JsonValue content
            && content.TryGetValue<string>(out var text)
            && text.Length > maxChars)
            clone["content"] = ClipText(text, maxChars, $" [clipped from {text.Length} characters for the summary input]");

        if (clone["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls.OfType<JsonObject>())
            {
                if (call["function"] is JsonObject function
                    && function["arguments"] is JsonValue arguments
                    && arguments.TryGetValue<string>(out var argumentText)
                    && argumentText.Length > maxChars)
                    function["arguments"] = ClipText(argumentText, maxChars, " [clipped]");
            }
        }

        return clone;
    }

    private static string ClipText(string text, int maxChars, string note)
    {
        if (text.Length <= maxChars)
            return text;
        // Never end on half of a surrogate pair: the JSON writer rejects invalid UTF-16.
        var end = maxChars > 0 && char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..end] + note;
    }

    /// <summary>
    /// Replaces large tool results with a pointer, except for the newest tool-call group, which the
    /// model is still reading. Returns how many results were replaced.
    /// </summary>
    internal static int ElideOlderToolResults(IList<JsonObject> messages, int threshold)
    {
        var protectedFrom = messages.Count;
        for (var i = messages.Count - 1; i >= 1; i--)
        {
            if (string.Equals(messages[i]["role"]?.GetValue<string>(), "assistant", StringComparison.Ordinal)
                && messages[i]["tool_calls"] is JsonArray { Count: > 0 })
            {
                protectedFrom = i;
                break;
            }
        }

        var elided = 0;
        for (var i = 1; i < protectedFrom; i++)
        {
            var message = messages[i];
            if (!string.Equals(message["role"]?.GetValue<string>(), "tool", StringComparison.Ordinal))
                continue;
            if (message["content"] is not JsonValue content
                || !content.TryGetValue<string>(out var text)
                || text.Length <= threshold)
                continue;
            message["content"] = $"[Older tool result elided to fit the context window: {text.Length} characters. Re-run the tool if the details are needed.]";
            elided++;
        }

        return elided;
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

    private static string BuildFallbackCheckpoint(
        IReadOnlyList<JsonObject> olderMessages,
        IReadOnlyList<NativeToolJournalEntry> toolJournal)
    {
        var recentUserMessages = olderMessages
            .Where(message => string.Equals(message["role"]?.GetValue<string>(), "user", StringComparison.Ordinal))
            .Select(message => message["content"]?.ToString() ?? "")
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .TakeLast(6)
            .ToArray();
        var recentAssistantNotes = olderMessages
            .Where(message => string.Equals(message["role"]?.GetValue<string>(), "assistant", StringComparison.Ordinal)
                && message["tool_calls"] is not JsonArray)
            .Select(message => message["content"]?.ToString() ?? "")
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .TakeLast(4)
            .ToArray();
        // An earlier checkpoint lives in a system message, which the user/assistant filters above skip;
        // without carrying it over, repeated fallbacks would silently discard the oldest history.
        var previousCheckpoint = olderMessages
            .Where(IsCheckpointMessage)
            .Select(message => message["content"]!.GetValue<string>()[CheckpointHeader.Length..].Trim())
            .LastOrDefault();

        return new JsonObject
        {
            ["goal"] = recentUserMessages.LastOrDefault() ?? "Continue the user's current request from the retained conversation.",
            ["previousCheckpoint"] = previousCheckpoint is null ? null : ClipText(previousCheckpoint, 6000, " [clipped]"),
            ["constraints"] = new JsonArray(),
            ["completed"] = JsonSerializer.SerializeToNode(recentAssistantNotes, NativeAgentRuntimeSerialization.JsonOptions),
            ["inProgress"] = new JsonArray(),
            ["remaining"] = new JsonArray(),
            ["decisions"] = new JsonArray(),
            ["importantIdentifiers"] = new JsonArray(),
            ["importantFiles"] = new JsonArray(),
            ["toolResults"] = JsonSerializer.SerializeToNode(toolJournal, NativeAgentRuntimeSerialization.JsonOptions),
            ["unresolvedQuestions"] = new JsonArray(),
            ["recentUserMessages"] = JsonSerializer.SerializeToNode(recentUserMessages, NativeAgentRuntimeSerialization.JsonOptions),
            ["nextAction"] = "Continue from the retained recent messages. Consult toolResults before repeating an operation.",
            ["fallback"] = true
        }.ToJsonString();
    }

    private static int EstimateTokens(IReadOnlyList<JsonObject> messages) =>
        Math.Max(1, Encoding.UTF8.GetByteCount(string.Join("\n", messages.Select(m => m.ToJsonString()))) / 4);

}
