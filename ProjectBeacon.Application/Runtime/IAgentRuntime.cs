namespace ProjectBeacon.Application.Runtime;

public sealed record AgentMessagePart(string Role, string Kind, string Body, string? ExternalId);

public sealed record AgentSessionUsage(int PromptTokens, int CompletionTokens, int AssistantMessages, double TotalCost);

public sealed record AgentPromptControls(double? Temperature, string? ReasoningEffort, string? ToolPermissions);

public interface IAgentRuntime : IAsyncDisposable
{
    Task<string> CreateSessionAsync(string title, CancellationToken ct);

    Task SendPromptAsync(string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null);

    IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(string sessionId, CancellationToken ct);

    Task AbortAsync(string sessionId, CancellationToken ct);

    Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct);
}
