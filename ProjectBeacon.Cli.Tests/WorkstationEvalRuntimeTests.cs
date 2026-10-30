namespace ProjectBeacon.Cli.Tests;

using System.Runtime.CompilerServices;
using System.Text.Json;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;

public sealed class WorkstationEvalRuntimeTests
{
    [Fact]
    public async Task RunEvalTurn_UsesPinnedNativeAuthorizationAndNativeTranscriptReference()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-eval-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var runtime = new CapturingRuntime();
        var authorization = new AgentHarnessAuthorization(
            "https://beacon.test",
            "one-shot-token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            root,
            DateTime.UtcNow.AddMinutes(2));

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                evalRunId = Guid.NewGuid(),
                prompt = "Read a file and report.",
                path = ".",
                model = "beacon-local/test-model",
                runtime = "native"
            });

            var result = await WorkstationEval.RunEvalTurnAsync(
                root,
                payload,
                null,
                runtime,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken,
                authorization,
                "native");

            Assert.True(result.Success, result.Error);
            Assert.Same(authorization, runtime.Controls!.HarnessAuthorization);
            Assert.Equal("beacon-local/test-model", runtime.Model);
            using var document = JsonDocument.Parse(result.Value!);
            Assert.StartsWith("native:session/", document.RootElement.GetProperty("transcriptRef").GetString());
            Assert.False(document.RootElement.GetProperty("interrupted").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class CapturingRuntime : IAgentRuntime, IAgentRuntimeCapabilities
    {
        public AgentPromptControls? Controls { get; private set; }
        public string? Model { get; private set; }

        public bool SupportsForms => false;
        public bool SupportsAgentSwitch => false;
        public bool SupportsReasoningEffort => true;
        public bool SupportsLiveDeltas => true;

        public Task<string> CreateSessionAsync(string title, CancellationToken ct)
            => Task.FromResult("1234567890abcdef1234567890abcdef");

        public Task SendPromptAsync(string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null)
        {
            Model = model;
            Controls = controls;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(
            string sessionId,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            yield return new AgentMessagePart("assistant", "text", "Done.", "assistant-part-1");
            await Task.CompletedTask;
        }

        public Task<AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<AgentTurnStatus?>(new AgentTurnStatus(true, "stop", null, "assistant-part-1"));

        public Task AbortAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(new AgentSessionUsage(10, 5, 1, 0));

        public Task<AgentRuntimeReadiness> EnsureReadyAsync(string? workingDirectory, CancellationToken ct)
            => Task.FromResult(new AgentRuntimeReadiness(true, null, workingDirectory));

        public Task StreamTextAsync(
            string sessionId,
            Func<string, string, int, string, CancellationToken, Task> onDelta,
            CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentRuntimeForm>> ListPendingFormsAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentRuntimeForm>>([]);

        public Task<bool> ReplyFormAsync(string sessionId, string formId, string answerJson, CancellationToken ct)
            => Task.FromResult(false);

        public Task SwitchAgentAsync(string sessionId, string agent, CancellationToken ct)
            => Task.CompletedTask;

        public Task<int?> ReadContextLimitAsync(string? modelId, CancellationToken ct)
            => Task.FromResult<int?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
