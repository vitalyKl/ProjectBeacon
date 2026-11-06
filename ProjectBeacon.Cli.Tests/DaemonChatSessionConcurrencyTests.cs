namespace ProjectBeacon.Cli.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Application.Runtime;
using ProjectBeacon.Cli.Client;

public sealed class DaemonChatSessionConcurrencyTests
{
    [Fact]
    public async Task NativeRuntime_AllowsAnotherChatWhileFirstSessionWaitsForFormAnswer()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-chat-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var http = new HttpClient(new SuccessHandler()) { BaseAddress = new Uri("http://localhost/") };
        await using var llama = new ClientLlamaSwap
        {
            SkipRealProcess = true,
            UseOwnSwapper = false,
            ConfigFile = Path.Combine(root, "llamaswap.yaml")
        };
        await using var openCode = new ClientOpenCodeServe { SkipRealProcess = true };
        var runtime = new ConcurrentSessionsRuntime();
        var deps = new DaemonDependencies(http, llama, openCode, runtime, () => new WorkstationSettings());
        var state = new DaemonRuntimeState("http://localhost", null);
        var gate = new DaemonOpenCodeGate(deps, runtime);
        var chat = new DaemonChatSession(deps, gate, new NoopLlamaSync(), state, new DaemonTiming
        {
            ChatPollInterval = TimeSpan.FromMilliseconds(1),
            ChatIdleTimeout = TimeSpan.FromSeconds(5),
            ChatMaxDuration = TimeSpan.FromSeconds(10)
        });

        try
        {
            var firstChatId = Guid.NewGuid();
            var first = chat.PromptAsync(root, PromptPayload(firstChatId, "native-session-1"), CancellationToken.None);
            await runtime.FirstFormObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(runtime.FirstFormAnswered);

            var second = chat.PromptAsync(root, PromptPayload(Guid.NewGuid(), "native-session-2"), CancellationToken.None);
            await runtime.BothPromptsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.False(runtime.FirstFormAnswered);
            var (replyOk, _, replyError) = await chat.ReplyFormAsync(
                JsonSerializer.Serialize(new
                {
                    externalSessionId = "native-session-1",
                    formId = "question-1",
                    answerJson = "{}"
                }),
                CancellationToken.None);
            Assert.True(replyOk, replyError);

            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.All(results, result => Assert.True(result.Ok, result.Error));
            Assert.True(runtime.FirstFormAnswered);
            Assert.Equal(2, runtime.Started);
        }
        finally
        {
            await chat.DisposeAsync();
            gate.Release();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string PromptPayload(Guid chatId, string externalSessionId) => JsonSerializer.Serialize(new
    {
        chatSessionId = chatId,
        externalSessionId,
        text = "continue independently",
        model = "test-model"
    });

    private sealed class ConcurrentSessionsRuntime : IAgentRuntime, IAgentRuntimeCapabilities
    {
        private readonly ConcurrentDictionary<string, int> _statusCalls = new(StringComparer.Ordinal);
        private int _started;
        private int _firstFormAnswered;

        public int Started => Volatile.Read(ref _started);
        public bool FirstFormAnswered => Volatile.Read(ref _firstFormAnswered) != 0;
        public TaskCompletionSource<bool> BothPromptsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstFormObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SupportsForms => true;
        public bool SupportsAgentSwitch => false;
        public bool SupportsReasoningEffort => true;
        public bool SupportsLiveDeltas => false;
        public bool SupportsConcurrentSessions => true;

        public Task<string> CreateSessionAsync(string title, CancellationToken ct)
            => Task.FromResult(Guid.NewGuid().ToString("N"));

        public Task SendPromptAsync(
            string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null)
        {
            if (Interlocked.Increment(ref _started) == 2)
                BothPromptsStarted.TrySetResult(true);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(
            string sessionId, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AgentMessagePart("assistant", "text", "finished", sessionId + ":text", true);
            await Task.CompletedTask;
        }

        public Task<AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct)
        {
            if (sessionId == "native-session-1" && !FirstFormAnswered)
                return Task.FromResult<AgentTurnStatus?>(new AgentTurnStatus(false, null, null, "baseline:" + sessionId));

            var call = _statusCalls.AddOrUpdate(sessionId, 1, (_, previous) => previous + 1);
            var messageId = call == 1 ? "baseline:" + sessionId : "final:" + sessionId;
            return Task.FromResult<AgentTurnStatus?>(new AgentTurnStatus(true, "stop", null, messageId));
        }

        public Task AbortAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(new AgentSessionUsage(1, 1, 1, 0));

        public Task<AgentRuntimeReadiness> EnsureReadyAsync(string? workingDirectory, CancellationToken ct)
            => Task.FromResult(new AgentRuntimeReadiness(true, null, workingDirectory));

        public Task StreamTextAsync(
            string sessionId,
            Func<string, string, int, string, CancellationToken, Task> onDelta,
            CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AgentRuntimeForm>> ListPendingFormsAsync(string sessionId, CancellationToken ct)
        {
            if (sessionId == "native-session-1" && !FirstFormAnswered)
            {
                FirstFormObserved.TrySetResult(true);
                return Task.FromResult<IReadOnlyList<AgentRuntimeForm>>(
                    [new AgentRuntimeForm("question-1", "Need an answer", "{}", "[]")]);
            }

            return Task.FromResult<IReadOnlyList<AgentRuntimeForm>>([]);
        }

        public Task<bool> ReplyFormAsync(string sessionId, string formId, string answerJson, CancellationToken ct)
        {
            if (sessionId != "native-session-1" || formId != "question-1" || FirstFormAnswered)
                return Task.FromResult(false);
            Interlocked.Exchange(ref _firstFormAnswered, 1);
            return Task.FromResult(true);
        }

        public Task SwitchAgentAsync(string sessionId, string agent, CancellationToken ct)
            => Task.CompletedTask;

        public Task<int?> ReadContextLimitAsync(string? modelId, CancellationToken ct)
            => Task.FromResult<int?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SuccessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
    }

    private sealed class NoopLlamaSync : IDaemonLlamaSync
    {
        public Task SyncAsync(WorkstationSettings settings, CancellationToken ct) => Task.CompletedTask;
        public Task RunLockedAsync(Func<Task> action, CancellationToken ct) => action();
        public Task ReloadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UnloadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SyncNowAsync(CancellationToken ct) => Task.CompletedTask;
        public void Release() { }
    }
}
