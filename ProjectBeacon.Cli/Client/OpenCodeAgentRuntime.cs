namespace ProjectBeacon.Cli.Client;

using System.Runtime.CompilerServices;
using Application.Runtime;
/// <summary>
/// IAgentRuntime for workstation chat. It creates sessions and sends prompts through the local OpenCode server. It is not the pipeline spawner.
/// </summary>
public sealed class OpenCodeAgentRuntime : IAgentRuntime
{
    private readonly ClientOpenCodeServe _serve;

    public OpenCodeAgentRuntime(ClientOpenCodeServe serve) => _serve = serve;

    public Task<string> CreateSessionAsync(string title, CancellationToken ct)
        => _serve.CreateSessionAsync(title, ct);

    public Task SendPromptAsync(string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null)
        => _serve.PromptAsync(sessionId, text, model, controls, ct);

    public async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(string sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var part in await _serve.ListPartsAsync(sessionId, ct))
            yield return new AgentMessagePart(part.Role, part.Kind, part.Body, part.ExternalId);
    }

    public Task AbortAsync(string sessionId, CancellationToken ct)
        => _serve.AbortAsync(sessionId, ct);

    public async Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
    {
        var usage = await _serve.ReadUsageAsync(sessionId, ct);
        return new AgentSessionUsage(usage.PromptTokens, usage.CompletionTokens, usage.AssistantMessages, usage.TotalCost);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
