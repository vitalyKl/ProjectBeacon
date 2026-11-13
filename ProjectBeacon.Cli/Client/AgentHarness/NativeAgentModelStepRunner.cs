namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;

/// <summary>
/// Executes exactly one assistant response step: streams provider events, records usage and
/// deltas, applies bounded retries, and retries once after context compaction.
/// It does not execute tools or own the lifetime of a complete turn.
/// </summary>
internal sealed class NativeAgentModelStepRunner(
    NativeAgentContextBuilder contextBuilder,
    Func<NativeAgentSessionState, int, string, string, int, string, CancellationToken, Task> appendDelta,
    Action<string>? log)
{
    private const int MaxAttempts = 5;
    private readonly NativeAgentContextBuilder _contextBuilder = contextBuilder;
    private readonly Func<NativeAgentSessionState, int, string, string, int, string, CancellationToken, Task> _appendDelta = appendDelta;
    private readonly Action<string>? _log = log;

    public async Task<NativeAgentModelStepResult> ExecuteAsync(
        NativeAgentSessionState session,
        int turnNumber,
        int stepOrdinal,
        string turnMessageId,
        string model,
        JsonArray tools,
        AgentPromptControls? controls,
        Func<ChatProviderRequest, CancellationToken, Task<string>> summarize,
        Func<IChatProvider> createProvider,
        Func<Func<Task>, CancellationToken, Task> runModelRequest,
        long firstTokenAt,
        bool emittedContent,
        CancellationToken ct)
    {
        var stepMessageId = $"{turnMessageId}:step:{stepOrdinal}";
        var partOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        string? currentPartKind = null;
        var currentPartOrdinal = -1;

        int GetPartOrdinal(string kind)
        {
            if (string.Equals(currentPartKind, kind, StringComparison.Ordinal))
                return currentPartOrdinal;
            currentPartKind = kind;
            currentPartOrdinal = partOrdinals.TryGetValue(kind, out var previous) ? previous + 1 : 0;
            partOrdinals[kind] = currentPartOrdinal;
            return currentPartOrdinal;
        }

        var messages = await _contextBuilder.BuildMessagesAsync(session, model, controls, tools, summarize, ct);
        var requestMessageCount = messages.Count;
        var request = new ChatProviderRequest(
            model,
            messages,
            (JsonArray)tools.DeepClone(),
            controls?.Temperature,
            controls?.ReasoningEffort,
            controls?.MaxTokens);

        var stepText = new StringBuilder();
        var toolCalls = new SortedDictionary<int, NativeToolCallAccumulator>();
        var finishReason = "stop";
        var requestCompleted = false;
        var contextOverflowRetryUsed = false;

        for (var attempt = 1; attempt <= MaxAttempts && !requestCompleted; attempt++)
        {
            var stepEmittedContent = false;
            var requestPromptTokens = 0;
            var requestCompletionTokens = 0;
            var estimatedPromptTokens = NativeAgentContextBuilder.EstimatePromptTokens(
                request.Messages.OfType<JsonObject>().ToList(),
                NativeAgentContextBuilder.EstimateToolTokens(request.Tools),
                0,
                0);

            async Task StreamProviderAttemptAsync()
            {
                var provider = createProvider();
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
                            await _appendDelta(
                                session, turnNumber, stepMessageId, "text",
                                GetPartOrdinal("text"), item.Text, ct);
                            break;

                        case ChatProviderEventKind.ReasoningDelta when !string.IsNullOrEmpty(item.Text):
                            firstTokenAt = firstTokenAt == 0 ? Stopwatch.GetTimestamp() : firstTokenAt;
                            stepEmittedContent = true;
                            emittedContent = true;
                            await _appendDelta(
                                session, turnNumber, stepMessageId, "reasoning",
                                GetPartOrdinal("reasoning"), item.Text, ct);
                            break;

                        case ChatProviderEventKind.Usage:
                            var promptUsage = Math.Max(0, item.PromptTokens ?? 0);
                            var completionUsage = Math.Max(0, item.CompletionTokens ?? 0);
                            requestPromptTokens = Math.Max(requestPromptTokens, promptUsage);
                            requestCompletionTokens = Math.Max(requestCompletionTokens, completionUsage);
                            lock (session.Sync)
                            {
                                session.Usage = session.Usage with
                                {
                                    PromptTokens = session.Usage.PromptTokens + promptUsage,
                                    CompletionTokens = session.Usage.CompletionTokens + completionUsage,
                                    PeakPromptTokens = Math.Max(session.Usage.PeakPromptTokens, promptUsage),
                                    MaxCompletionTokens = Math.Max(session.Usage.MaxCompletionTokens, completionUsage)
                                };
                                if (item.PromptTokens is > 0)
                                {
                                    session.LastPromptTokens = item.PromptTokens.Value;
                                    session.LastPromptMessageCount = requestMessageCount;
                                }
                            }
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
                                accumulator = new NativeToolCallAccumulator(index);
                                toolCalls.Add(index, accumulator);
                            }
                            accumulator.Append(item.ToolCallId, item.ToolName, item.ToolArguments);
                            break;
                    }
                }
            }

            try
            {
                await runModelRequest(StreamProviderAttemptAsync, ct);
                requestCompleted = true;
                var usageSource = requestPromptTokens > 0 ? "provider" : "estimate";
                var promptForLog = requestPromptTokens > 0 ? requestPromptTokens : estimatedPromptTokens;
                lock (session.Sync)
                {
                    session.Usage = session.Usage with
                    {
                        PeakPromptTokens = Math.Max(session.Usage.PeakPromptTokens, promptForLog)
                    };
                }
                _log?.Invoke(
                    $"native usage turn={turnNumber} step={stepOrdinal + 1} attempt={attempt} " +
                    $"prompt={promptForLog} completion={requestCompletionTokens} source={usageSource}");
            }
            catch (Exception ex) when (IsContextLengthExceeded(ex)
                && attempt < MaxAttempts
                && !contextOverflowRetryUsed
                && !stepEmittedContent
                && !ct.IsCancellationRequested)
            {
                contextOverflowRetryUsed = true;
                var rejectedPromptTokens = requestPromptTokens > 0 ? requestPromptTokens : estimatedPromptTokens;
                lock (session.Sync)
                {
                    session.Usage = session.Usage with
                    {
                        PeakPromptTokens = Math.Max(session.Usage.PeakPromptTokens, rejectedPromptTokens)
                    };
                }
                _log?.Invoke(
                    $"native provider rejected turn={turnNumber} step={stepOrdinal + 1} attempt={attempt} " +
                    $"for context length (prompt about {rejectedPromptTokens} tokens); compacting and retrying once");
                request = request with
                {
                    Messages = await _contextBuilder.BuildMessagesAsync(
                        session, model, controls, tools, summarize, ct, forceCompaction: true)
                };
                requestMessageCount = request.Messages.Count;
            }
            catch (Exception ex) when (IsRetryable(ex)
                && attempt < MaxAttempts
                && !stepEmittedContent
                && !ct.IsCancellationRequested)
            {
                var delaySeconds = Math.Min(8, 1 << (attempt - 1));
                _log?.Invoke(
                    $"native provider request failed transiently (attempt {attempt}/{MaxAttempts}); " +
                    $"retrying in {delaySeconds}s");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            }
        }

        if (!requestCompleted)
            throw new HttpRequestException("Chat provider did not complete the request.");

        return new NativeAgentModelStepResult(
            stepText.ToString(),
            toolCalls.Values.ToArray(),
            finishReason,
            firstTokenAt,
            emittedContent);
    }

    private static bool IsContextLengthExceeded(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            if (current is HttpRequestException { StatusCode: { } status } && (int)status == 413)
                return true;
            if (ChatProviderException.IsContextLengthExceededText(current.Message))
                return true;
        }
        return false;
    }

    private static bool IsRetryable(Exception ex) =>
        ex is ChatProviderException provider
            ? provider.Retryable
            : ex is HttpRequestException or IOException;
}
