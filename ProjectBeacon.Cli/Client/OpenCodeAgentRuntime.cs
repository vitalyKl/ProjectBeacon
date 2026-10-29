namespace ProjectBeacon.Cli.Client;

using System.Runtime.CompilerServices;
using Application.Runtime;
/// <summary>
/// IAgentRuntime for workstation chat. It creates sessions and sends prompts through the local OpenCode server. It is not the pipeline spawner.
/// </summary>
public sealed class OpenCodeAgentRuntime : IAgentRuntime, IAgentRuntimeCapabilities
{
    private readonly ClientOpenCodeServe _serve;

    public OpenCodeAgentRuntime(ClientOpenCodeServe serve) => _serve = serve;

    public bool SupportsForms => _serve.SupportsV2Api;
    public bool SupportsAgentSwitch => _serve.SupportsV2Api;
    public bool SupportsReasoningEffort => true;
    public bool SupportsLiveDeltas => _serve.SupportsV2Api;

    public Task<string> CreateSessionAsync(string title, CancellationToken ct)
        => _serve.CreateSessionAsync(title, ct);

    public Task SendPromptAsync(string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null)
        => _serve.PromptAsync(sessionId, text, model, controls, ct);

    public async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(string sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var part in await _serve.ListPartsAsync(sessionId, ct))
            yield return new AgentMessagePart(part.Role, part.Kind, part.Body, part.ExternalId, part.Complete, part.Tool, part.ToolStatus, part.ToolInput, part.ToolResult, part.DurationMs);
    }

    public Task AbortAsync(string sessionId, CancellationToken ct)
        => _serve.AbortAsync(sessionId, ct);

    public async Task<AgentRuntimeReadiness> EnsureReadyAsync(string? workingDirectory, CancellationToken ct)
    {
        await _serve.TickAsync(workingDirectory, ct);
        return new AgentRuntimeReadiness(_serve.Status.Healthy, _serve.Status.Error, _serve.Cwd);
    }

    public Task StreamTextAsync(string sessionId, Func<string, string, int, string, CancellationToken, Task> onDelta, CancellationToken ct)
        => _serve.StreamSessionTextAsync(sessionId, onDelta, ct);

    public async Task<IReadOnlyList<AgentRuntimeForm>> ListPendingFormsAsync(string sessionId, CancellationToken ct)
        => (await _serve.ListPendingFormsAsync(sessionId, ct))
            .Select(form => new AgentRuntimeForm(form.Id, form.Title, form.Metadata, form.FieldsJson))
            .ToArray();

    public Task<bool> ReplyFormAsync(string sessionId, string formId, string answerJson, CancellationToken ct)
        => _serve.ReplyFormAsync(sessionId, formId, answerJson, ct);

    public Task SwitchAgentAsync(string sessionId, string agent, CancellationToken ct)
        => _serve.SwitchAgentAsync(sessionId, agent, ct);

    public Task<int?> ReadContextLimitAsync(string? modelId, CancellationToken ct)
        => _serve.ReadContextLimitAsync(modelId, ct);

    public Task<AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct)
        => _serve.GetTurnStatusAsync(sessionId, ct);

    public async Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
    {
        var usage = await _serve.ReadUsageAsync(sessionId, ct);
        return new AgentSessionUsage(usage.PromptTokens, usage.CompletionTokens, usage.AssistantMessages, usage.TotalCost, usage.GenerationSeconds);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
