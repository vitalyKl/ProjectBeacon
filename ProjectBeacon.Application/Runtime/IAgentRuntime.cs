namespace ProjectBeacon.Application.Runtime;

/// <summary>One streamed part of an agent session. Complete means the message is final and the body is full.
/// Tool fields describe a tool call part; they are null for text parts and legacy rows.</summary>
public sealed record AgentMessagePart(
    string Role, string Kind, string Body, string? ExternalId, bool Complete = true,
    string? Tool = null, string? ToolStatus = null, string? ToolInput = null, string? ToolResult = null, int? DurationMs = null);

/// <summary>Completion state of the session's last assistant message; MessageId identifies that message.</summary>
public sealed record AgentTurnStatus(bool Ended, string? Finish, string? Error, string? MessageId = null);

/// <summary>Token, message, and cost totals read back from an agent session.</summary>
public sealed record AgentSessionUsage(int PromptTokens, int CompletionTokens, int AssistantMessages, double TotalCost, double GenerationSeconds = 0);

/// <summary>Optional temperature, reasoning effort, and tool permissions sent with a prompt.</summary>
public sealed record AgentPromptControls(double? Temperature, string? ReasoningEffort, string? ToolPermissions)
{
    public int? ContextLimit { get; init; }
    public int? MaxTokens { get; init; }
}

/// <summary>Readiness reported by a workstation-local agent runtime.</summary>
public sealed record AgentRuntimeReadiness(bool Ready, string? Error, string? WorkingDirectory);

/// <summary>A pending user question exposed by a runtime that supports interactive forms.</summary>
public sealed record AgentRuntimeForm(string Id, string Title, string Metadata, string FieldsJson);

/// <summary>Optional runtime operations beyond the minimum chat/session contract.</summary>
public interface IAgentRuntimeCapabilities
{
    bool SupportsForms { get; }
    bool SupportsAgentSwitch { get; }
    bool SupportsReasoningEffort { get; }
    bool SupportsLiveDeltas { get; }

    Task<AgentRuntimeReadiness> EnsureReadyAsync(string? workingDirectory, CancellationToken ct);
    Task StreamTextAsync(string sessionId, Func<string, string, int, string, CancellationToken, Task> onDelta, CancellationToken ct);
    Task<IReadOnlyList<AgentRuntimeForm>> ListPendingFormsAsync(string sessionId, CancellationToken ct);
    Task<bool> ReplyFormAsync(string sessionId, string formId, string answerJson, CancellationToken ct);
    Task SwitchAgentAsync(string sessionId, string agent, CancellationToken ct);
    Task<int?> ReadContextLimitAsync(string? modelId, CancellationToken ct);
}

/// <summary>Creates an agent session, sends a prompt, streams parts, reports turn state, aborts, and reads usage.</summary>
public interface IAgentRuntime : IAsyncDisposable
{
    Task<string> CreateSessionAsync(string title, CancellationToken ct);

    Task SendPromptAsync(string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null);

    IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(string sessionId, CancellationToken ct);

    Task<AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct);

    Task AbortAsync(string sessionId, CancellationToken ct);

    Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct);
}
