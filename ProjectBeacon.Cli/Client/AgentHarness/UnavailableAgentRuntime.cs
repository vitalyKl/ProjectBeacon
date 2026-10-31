namespace ProjectBeacon.Cli.Client.AgentHarness;

using ProjectBeacon.Application.Runtime;

/// <summary>
/// Fail-closed runtime used when the configured workstation runtime cannot be resolved.
/// It deliberately never delegates to OpenCode.
/// </summary>
internal sealed class UnavailableAgentRuntime(string error) : IAgentRuntime, IAgentRuntimeCapabilities
{
    public string Error { get; } = string.IsNullOrWhiteSpace(error)
        ? "The selected agent runtime is unavailable."
        : error;

    public bool SupportsForms => false;
    public bool SupportsAgentSwitch => false;
    public bool SupportsReasoningEffort => false;
    public bool SupportsLiveDeltas => false;

    public Task<AgentRuntimeReadiness> EnsureReadyAsync(string? workingDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentRuntimeReadiness(false, Error, workingDirectory));
    }

    public Task<string> CreateSessionAsync(string title, CancellationToken ct) =>
        Task.FromException<string>(CreateException());

    public Task SendPromptAsync(
        string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null) =>
        Task.FromException(CreateException());

    public IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(string sessionId, CancellationToken ct) =>
        throw CreateException();

    public Task<AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct) =>
        Task.FromException<AgentTurnStatus?>(CreateException());

    public Task AbortAsync(string sessionId, CancellationToken ct) =>
        Task.FromException(CreateException());

    public Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct) =>
        Task.FromException<AgentSessionUsage>(CreateException());

    public Task StreamTextAsync(
        string sessionId,
        Func<string, string, int, string, CancellationToken, Task> onDelta,
        CancellationToken ct) =>
        Task.FromException(CreateException());

    public Task<IReadOnlyList<AgentRuntimeForm>> ListPendingFormsAsync(string sessionId, CancellationToken ct) =>
        Task.FromException<IReadOnlyList<AgentRuntimeForm>>(CreateException());

    public Task<bool> ReplyFormAsync(string sessionId, string formId, string answerJson, CancellationToken ct) =>
        Task.FromException<bool>(CreateException());

    public Task SwitchAgentAsync(string sessionId, string agent, CancellationToken ct) =>
        Task.FromException(CreateException());

    public Task<int?> ReadContextLimitAsync(string? modelId, CancellationToken ct) =>
        Task.FromException<int?>(CreateException());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private InvalidOperationException CreateException() => new(Error);
}
