namespace ProjectBeacon.Application.Runtime;

/// <summary>One streamed part of an agent session. Complete means the message is final and the body is full.
/// Tool fields describe a tool call part; they are null for text parts and legacy rows.</summary>
public sealed record AgentMessagePart(
    string Role, string Kind, string Body, string? ExternalId, bool Complete = true,
    string? Tool = null, string? ToolStatus = null, string? ToolInput = null, string? ToolResult = null, int? DurationMs = null);

/// <summary>Completion state of the session's last assistant message; MessageId identifies that message.</summary>
public sealed record AgentTurnStatus(bool Ended, string? Finish, string? Error, string? MessageId = null);

/// <summary>Token, message, and cost totals read back from an agent session.</summary>
public sealed record AgentSessionUsage(int PromptTokens, int CompletionTokens, int AssistantMessages, double TotalCost);

/// <summary>Optional temperature, reasoning effort, and tool permissions sent with a prompt.</summary>
public sealed record AgentPromptControls(double? Temperature, string? ReasoningEffort, string? ToolPermissions);

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
