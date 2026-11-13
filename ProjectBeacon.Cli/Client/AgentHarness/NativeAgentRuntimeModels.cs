namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Mutable state for one workstation-local agent session. The state is shared by the runtime
/// façade and the focused session, context, turn, and tool services.
/// </summary>
internal sealed class NativeAgentSessionState
{
    public object Sync { get; } = new();
    public SemaphoreSlim Persistence { get; } = new(1, 1);
    public SemaphoreSlim CommandPermissionGate { get; } = new(1, 1);
    public ConcurrentDictionary<string, NativePendingFormState> PendingForms { get; } = new(StringComparer.Ordinal);
    public Channel<NativeDelta> Deltas { get; } = Channel.CreateUnbounded<NativeDelta>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string AgentMode { get; set; } = "build";
    public DateTimeOffset CreatedAt { get; set; }
    public string TranscriptPath { get; set; } = "";
    public List<JsonObject> Messages { get; set; } = [];
    public List<AgentMessagePart> Parts { get; set; } = [];
    public int TurnNumber { get; set; }
    public string? LastModel { get; set; }
    public int? ContextLimit { get; set; }

    // Runtime-only provider usage from the preceding request. Restored sessions start with estimates.
    public int LastPromptTokens { get; set; }
    public int LastPromptMessageCount { get; set; }
    public bool ContextLimitWarningLogged { get; set; }

    public AgentSessionUsage Usage { get; set; } = new(0, 0, 0, 0);
    public AgentTurnStatus? TurnStatus { get; set; }
    public List<NativeToolJournalEntry> ToolJournal { get; set; } = [];
    public CancellationTokenSource? ActiveTurn { get; set; }
    public Task? ActiveTask { get; set; }
}

/// <summary>Transient form state and the callback used to resume a queued turn after approval.</summary>
internal sealed class NativePendingFormState(AgentRuntimeForm form, TaskCompletionSource<string> completion)
{
    private readonly object _sync = new();
    private Action? _resumeCallback;

    public AgentRuntimeForm Form { get; } = form;
    public TaskCompletionSource<string> Completion { get; } = completion;

    public void RegisterResumeCallback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var alreadyCompleted = false;
        lock (_sync)
        {
            if (Completion.Task.IsCompleted)
                alreadyCompleted = true;
            else
                _resumeCallback = callback;
        }
        if (alreadyCompleted)
            callback();
    }

    public bool TrySetResult(string answerJson)
    {
        lock (_sync)
        {
            if (Completion.Task.IsCompleted)
                return false;

            // Resume the owning task before completing the form's awaitable result.
            _resumeCallback?.Invoke();
            _resumeCallback = null;
            return Completion.TrySetResult(answerJson);
        }
    }
}

internal sealed record NativeDelta(
    int TurnNumber,
    string Kind,
    string MessageId,
    int Ordinal,
    string Text,
    bool IsTurnEnd)
{
    public static NativeDelta End(int turn) => new(turn, "", "", 0, "", true);
}

internal sealed record NativeToolJournalEntry(
    int TurnNumber,
    string Tool,
    string ArgumentsJson,
    string Result,
    bool IsError,
    bool CacheHit,
    DateTimeOffset RecordedAt);

/// <summary>Assembled tool call emitted by a streaming provider response.</summary>
internal sealed class NativeToolCallAccumulator(int index)
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

internal sealed record NativeResolvedProvider(
    string ProviderId, string BaseUrl, string? ApiKey, string Model, bool IsLocal);

internal sealed record NativeExecutedToolCall(
    NativeToolCallAccumulator Call,
    string Result,
    bool IsError,
    int DurationMs,
    bool CacheHit);

internal sealed record NativeAgentToolCatalogResult(
    JsonArray Tools,
    string? ManifestFingerprint,
    bool ManifestTrustedAtTurnStart);

internal sealed record NativeToolExecutionContext(
    NativeAgentSessionState Session,
    string AgentMode,
    AgentPromptControls? Controls,
    IMcpToolClient? McpClient,
    JsonArray AdvertisedTools,
    bool RepeatedToolCall,
    ConcurrentDictionary<string, McpToolResult> ResultCache,
    IModelTaskLease TaskLease,
    string? ManifestFingerprint,
    bool ManifestTrustedAtTurnStart);

/// <summary>One canonical serializer configuration for native runtime state and model payloads.</summary>
internal static class NativeAgentRuntimeSerialization
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}


/// <summary>Assembled tool call emitted by a streaming provider response.</summary>
internal sealed record NativeAgentModelStepResult(
    string Text,
    IReadOnlyList<NativeToolCallAccumulator> ToolCalls,
    string FinishReason,
    long FirstTokenAt,
    bool EmittedContent);



/// <summary>Mutable counters and read cache scoped to one complete agent turn.</summary>
internal sealed class NativeAgentToolLoopState
{
    public string? LastToolCallFingerprint { get; set; }
    public int ConsecutiveToolCallFingerprintCount { get; set; }
    public int NextToolOrdinal { get; set; }
    public ConcurrentDictionary<string, McpToolResult> ResultCache { get; } = new(StringComparer.Ordinal);
}

/// <summary>Outcome of one tool-call batch in the assistant/tool loop.</summary>
internal sealed record NativeAgentToolBatchResult(bool IsTerminal, string? Error = null);
