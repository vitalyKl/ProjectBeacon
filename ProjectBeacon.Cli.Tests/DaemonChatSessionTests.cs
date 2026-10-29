using System.Net;
using System.Text.Json;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;

public sealed class DaemonChatSessionTests : IAsyncLifetime
{
    private readonly List<CapturedPost> _posts = [];
    private readonly HttpClient _http;
    private readonly ClientOpenCodeServe _openCode = new() { SkipRealProcess = true };
    private readonly ClientLlamaSwap _llama;
    private readonly string _dir;

    public DaemonChatSessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "beacon-dcs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _llama = new ClientLlamaSwap { SkipRealProcess = true, ConfigFile = Path.Combine(_dir, "config.yaml") };
        _http = new HttpClient(new CapturingHandler(_posts)) { BaseAddress = new Uri("http://localhost/") };
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _openCode.DisposeAsync();
        await _llama.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Prompt_SendsProjectHeaderOnPartsAndIdle()
    {
        var chatId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        await RunPromptAsync(JsonSerializer.Serialize(new
        {
            chatSessionId = chatId,
            externalSessionId = "oc-1",
            projectId,
            text = "hello",
            model = (string?)null
        }));

        var part = _posts.Single(p => p.Path == $"/v1/chat/sessions/{chatId}/parts");
        Assert.Equal(projectId.ToString(), part.ProjectHeader);
        var idle = _posts.Single(p => p.Path == $"/v1/chat/sessions/{chatId}/idle");
        Assert.Equal(projectId.ToString(), idle.ProjectHeader);
    }

    [Fact]
    public async Task Prompt_WithoutProject_OmitsHeader()
    {
        var chatId = Guid.NewGuid();
        await RunPromptAsync(JsonSerializer.Serialize(new
        {
            chatSessionId = chatId,
            externalSessionId = "oc-1",
            text = "hello",
            model = (string?)null
        }));

        Assert.NotEmpty(_posts);
        Assert.All(_posts, p => Assert.Null(p.ProjectHeader));
    }

    [Fact]
    public async Task Prompt_PublishesStreamedSnapshotThenFinalPartOnSameKey()
    {
        var chatId = Guid.NewGuid();
        _openCode.TestStream = (sessionId, onDelta, ct) =>
        {
            Assert.Equal("oc-1", sessionId);
            return onDelta("text", "m1", 0, "Hel", ct);
        };
        var timing = new DaemonTiming
        {
            ChatPollInterval = TimeSpan.FromMilliseconds(1),
            ChatIdleTimeout = TimeSpan.FromMilliseconds(1),
            DelayAsync = (_, ct) => Task.CompletedTask
        };
        var runtime = new FinalPartRuntime();
        var chat = new DaemonChatSession(
            new DaemonDependencies(_http, _llama, _openCode, runtime, () => new WorkstationSettings()),
            new StubGate(_openCode, runtime),
            new StubLlamaSync(),
            new StubState(),
            timing);
        var (ok, _, error) = await chat.PromptAsync(_dir, JsonSerializer.Serialize(new
        {
            chatSessionId = chatId,
            externalSessionId = "oc-1",
            text = "hello",
            model = (string?)null
        }), CancellationToken.None);
        Assert.True(ok, error);

        var partPosts = _posts.Where(p => p.Path == $"/v1/chat/sessions/{chatId}/parts").ToList();
        Assert.Equal(2, partPosts.Count);
        using var streamed = JsonDocument.Parse(partPosts[0].Body!);
        Assert.Equal("Hel", streamed.RootElement.GetProperty("body").GetString());
        Assert.Equal("m1:text:0", streamed.RootElement.GetProperty("externalId").GetString());
        Assert.False(streamed.RootElement.GetProperty("complete").GetBoolean());
        using var final = JsonDocument.Parse(partPosts[1].Body!);
        Assert.Equal("hello", final.RootElement.GetProperty("body").GetString());
        Assert.Equal("m1:text:0", final.RootElement.GetProperty("externalId").GetString());
        Assert.True(final.RootElement.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Prompt_PostsSessionUsageBeforeIdle()
    {
        var chatId = Guid.NewGuid();
        await RunPromptAsync(JsonSerializer.Serialize(new
        {
            chatSessionId = chatId,
            externalSessionId = "oc-1",
            text = "hello",
            model = (string?)null
        }));

        var usage = _posts.Single(p => p.Path == $"/v1/chat/sessions/{chatId}/usage");
        using var doc = JsonDocument.Parse(usage.Body!);
        Assert.Equal(1, doc.RootElement.GetProperty("promptTokens").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("completionTokens").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("assistantMessages").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("totalCost").GetDouble());
        Assert.Equal(2.5, doc.RootElement.GetProperty("generationSeconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("contextLimit").ValueKind);
        // The usage report lands before the idle marker so the UI shows totals as soon as idle arrives.
        var idle = _posts.Single(p => p.Path == $"/v1/chat/sessions/{chatId}/idle");
        Assert.True(_posts.IndexOf(usage) < _posts.IndexOf(idle));
    }

    [Fact]
    public async Task Prompt_StayAwakeUntilMaxDurationWhenNoPartsArrive()
    {
        var chatId = Guid.NewGuid();
        var runtime = new SilentRuntime();
        var timing = new DaemonTiming
        {
            ChatPollInterval = TimeSpan.FromMilliseconds(1),
            ChatIdleTimeout = TimeSpan.FromMilliseconds(1),
            ChatMaxDuration = TimeSpan.FromMilliseconds(10),
            DelayAsync = (_, ct) => Task.CompletedTask
        };
        var chat = new DaemonChatSession(
            new DaemonDependencies(_http, _llama, _openCode, runtime, () => new WorkstationSettings()),
            new StubGate(_openCode, runtime),
            new StubLlamaSync(),
            new StubState(),
            timing);
        var (ok, result, error) = await chat.PromptAsync(_dir, JsonSerializer.Serialize(new
        {
            chatSessionId = chatId,
            externalSessionId = "oc-1",
            text = "hello",
            model = (string?)null
        }), CancellationToken.None);
        Assert.True(ok, error);
        // The quiet timeout must not close a turn that has no assistant part yet: the loop
        // stopped only at the max duration (1 baseline + 10 polls).
        Assert.Contains(@"""interrupted"":true", result!);
        Assert.Equal(11, runtime.StatusCalls);
        Assert.DoesNotContain(_posts, x => x.Path == $"/v1/chat/sessions/{chatId}/parts");
        Assert.Single(_posts, x => x.Path == $"/v1/chat/sessions/{chatId}/idle");
    }

    [Fact]
    public async Task Prompt_SurfacesPendingFormOnceWhileTurnRuns()
    {
        var chatId = Guid.NewGuid();
        _openCode.TestPendingForms = (sessionId, ct) => Task.FromResult<IReadOnlyList<OpenCodeFormInfo>>(
            sessionId == "oc-1"
                ? [new OpenCodeFormInfo("f1", "Pick an option", "{}", @"[{""key"":""choice"",""type"":""string""}]")]
                : []);
        var runtime = new LateEndRuntime();
        var timing = new DaemonTiming
        {
            ChatPollInterval = TimeSpan.FromMilliseconds(1),
            ChatIdleTimeout = TimeSpan.FromSeconds(10),
            DelayAsync = (_, ct) => Task.CompletedTask
        };
        var chat = new DaemonChatSession(
            new DaemonDependencies(_http, _llama, _openCode, runtime, () => new WorkstationSettings()),
            new StubGate(_openCode, runtime),
            new StubLlamaSync(),
            new StubState(),
            timing);
        var (ok, _, error) = await chat.PromptAsync(_dir, JsonSerializer.Serialize(new
        {
            chatSessionId = chatId,
            externalSessionId = "oc-1",
            text = "hello",
            model = (string?)null
        }), CancellationToken.None);
        Assert.True(ok, error);
        // The form is polled at i=0 and i=8; the second poll must dedupe it.
        var questionPosts = _posts
            .Where(x => x.Path == $"/v1/chat/sessions/{chatId}/parts" && x.Body!.Contains("question"))
            .ToList();
        Assert.Single(questionPosts);
        using var doc = JsonDocument.Parse(questionPosts[0].Body!);
        Assert.Equal("form:f1", doc.RootElement.GetProperty("externalId").GetString());
        Assert.True(doc.RootElement.GetProperty("complete").GetBoolean());
        using var body = JsonDocument.Parse(doc.RootElement.GetProperty("body").GetString()!);
        Assert.Equal("f1", body.RootElement.GetProperty("id").GetString());
        Assert.Equal("Pick an option", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("string", body.RootElement.GetProperty("fields")[0].GetProperty("type").GetString());
        // The question must land before idle so the user can answer while the turn is live.
        Assert.True(_posts.IndexOf(questionPosts[0]) < _posts.IndexOf(_posts.Single(x => x.Path == $"/v1/chat/sessions/{chatId}/idle")));
    }

    [Fact]
    public async Task SwitchAgent_PassesThroughToServe()
    {
        var chat = BuildSession(_openCode);
        var (ok, result, error) = await chat.SwitchAgentAsync(JsonSerializer.Serialize(new
        {
            externalSessionId = "oc-1",
            chatSessionId = Guid.NewGuid(),
            projectId = Guid.NewGuid(),
            agent = "plan"
        }), CancellationToken.None);
        Assert.True(ok, error);
        Assert.Equal("{}", result);
    }

    [Fact]
    public async Task SwitchAgent_MissingAgent_Rejected()
    {
        var chat = BuildSession(_openCode);
        var (ok, _, error) = await chat.SwitchAgentAsync(JsonSerializer.Serialize(new
        {
            externalSessionId = "oc-1",
            agent = (string?)null
        }), CancellationToken.None);
        Assert.False(ok);
        Assert.Equal("externalSessionId and agent are required.", error);
    }

    [Fact]
    public async Task SwitchAgent_FailurePostsErrorPart()
    {
        var chatId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        // A v1 serve has no agent endpoint; the failure must surface as a chat error part.
        await using var failServe = new ClientOpenCodeServe { SkipRealProcess = false };
        var chat = BuildSession(failServe);
        var (ok, _, error) = await chat.SwitchAgentAsync(JsonSerializer.Serialize(new
        {
            externalSessionId = "oc-1",
            chatSessionId = chatId,
            projectId,
            agent = "plan"
        }), CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("only supported by OpenCode 2.x", error!);
        var part = _posts.Single(x => x.Path == $"/v1/chat/sessions/{chatId}/parts");
        Assert.Equal(projectId.ToString(), part.ProjectHeader);
        using var doc = JsonDocument.Parse(part.Body!);
        Assert.Equal("error", doc.RootElement.GetProperty("kind").GetString());
        Assert.StartsWith("agent:", doc.RootElement.GetProperty("externalId").GetString());
        Assert.Contains("only supported by OpenCode 2.x", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task ReplyForm_ReportsAlreadySettled()
    {
        var chatId = Guid.NewGuid();
        await using var serve = new ClientOpenCodeServe
        {
            SkipRealProcess = false,
            SendAsync = (request, _) =>
            {
                Assert.Equal("/api/session/oc-1/form/f1/reply", request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
            }
        };
        serve.HandleStdoutLine("server password secret-pw");
        var chat = BuildSession(serve);
        var (ok, result, error) = await chat.ReplyFormAsync(JsonSerializer.Serialize(new
        {
            externalSessionId = "oc-1",
            formId = "f1",
            answerJson = JsonSerializer.Serialize(new { choice = "b" })
        }), CancellationToken.None);
        Assert.True(ok, error);
        Assert.Contains("alreadySettled", result!);
        Assert.Contains("true", result!);
    }

    [Fact]
    public async Task ReplyForm_MissingPayload_Rejected()
    {
        var chat = BuildSession(_openCode);
        var (ok, _, error) = await chat.ReplyFormAsync(JsonSerializer.Serialize(new
        {
            externalSessionId = "oc-1",
            formId = (string?)null,
            answerJson = (string?)null
        }), CancellationToken.None);
        Assert.False(ok);
        Assert.Equal("externalSessionId, formId, and answerJson are required.", error);
    }

    private DaemonChatSession BuildSession(ClientOpenCodeServe serve)
    {
        var runtime = new StubRuntime();
        var timing = new DaemonTiming
        {
            ChatPollInterval = TimeSpan.FromMilliseconds(1),
            ChatIdleTimeout = TimeSpan.FromMilliseconds(1),
            DelayAsync = (_, ct) => Task.CompletedTask
        };
        return new DaemonChatSession(
            new DaemonDependencies(_http, _llama, serve, runtime, () => new WorkstationSettings()),
            new StubGate(serve, runtime),
            new StubLlamaSync(),
            new StubState(),
            timing);
    }

    private async Task RunPromptAsync(string payload)
    {
        var runtime = new StubRuntime();
        var timing = new DaemonTiming
        {
            ChatPollInterval = TimeSpan.FromMilliseconds(1),
            ChatIdleTimeout = TimeSpan.FromMilliseconds(1),
            DelayAsync = (_, ct) => Task.CompletedTask
        };
        var chat = new DaemonChatSession(
            new DaemonDependencies(_http, _llama, _openCode, runtime, () => new WorkstationSettings()),
            new StubGate(_openCode, runtime),
            new StubLlamaSync(),
            new StubState(),
            timing);
        var (ok, _, error) = await chat.PromptAsync(_dir, payload, CancellationToken.None);
        Assert.True(ok, error);
    }

    private sealed record CapturedPost(string Method, string Path, string? ProjectHeader, string? Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<CapturedPost> _posts;

        public CapturingHandler(List<CapturedPost> posts) => _posts = posts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            _posts.Add(new CapturedPost(request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("X-Project-Id", out var values) ? string.Join(",", values) : null, body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private class StubRuntime : IAgentRuntime
    {
        private int _statusCalls;

        public Task<string> CreateSessionAsync(string title, CancellationToken ct) => Task.FromResult("oc-1");

        public Task SendPromptAsync(string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null) => Task.CompletedTask;

        public virtual async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(string sessionId, CancellationToken ct)
        {
            yield return new AgentMessagePart("assistant", "text", "hello", "p1");
        }

        // The first call is the pre-prompt baseline; later calls report a new finished message.
        public Task<AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<AgentTurnStatus?>(Interlocked.Increment(ref _statusCalls) == 1
                ? new AgentTurnStatus(true, "stop", null, "m1")
                : new AgentTurnStatus(true, "stop", null, "m2"));

        public Task AbortAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(new AgentSessionUsage(1, 1, 1, 0, 2.5));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FinalPartRuntime : StubRuntime
    {
        // The poller sees the final text under the same kind-scoped key the stream used.
        public override async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(string sessionId, CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new AgentMessagePart("assistant", "text", "hello", "m1:text:0");
        }
    }

    /// <summary>No parts and no turn status ever: only the max duration may end the prompt.</summary>
    private sealed class SilentRuntime : IAgentRuntime
    {
        public int StatusCalls;

        public Task<string> CreateSessionAsync(string title, CancellationToken ct) => Task.FromResult("oc-1");

        public Task SendPromptAsync(string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null) => Task.CompletedTask;

        public async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(string sessionId, CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct)
        {
            Interlocked.Increment(ref StatusCalls);
            return Task.FromResult<AgentTurnStatus?>(null);
        }

        public Task AbortAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(new AgentSessionUsage(0, 0, 0, 0));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The turn ends on the 10th status call (poll i=8), so the form polls at i=0 and
    /// i=8 both see the pending form and the second must dedupe it.</summary>
    private sealed class LateEndRuntime : IAgentRuntime
    {
        public int StatusCalls;

        public Task<string> CreateSessionAsync(string title, CancellationToken ct) => Task.FromResult("oc-1");

        public Task SendPromptAsync(string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null) => Task.CompletedTask;

        public async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(string sessionId, CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref StatusCalls);
            return Task.FromResult<AgentTurnStatus?>(n >= 10
                ? new AgentTurnStatus(true, "stop", null, "m2")
                : new AgentTurnStatus(false, null, null, "m1"));
        }

        public Task AbortAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(new AgentSessionUsage(0, 0, 0, 0));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubGate : IDaemonOpenCodeGate
    {
        public StubGate(ClientOpenCodeServe openCode, IAgentRuntime runtime)
        {
            OpenCode = openCode;
            Runtime = runtime;
        }

        public ClientOpenCodeServe OpenCode { get; }
        public IAgentRuntime Runtime { get; }
        public Task TryTickAsync(string? projectsRoot, CancellationToken ct) => Task.CompletedTask;
        public Task RunExclusiveAsync(Func<Task> action, CancellationToken ct) => action();
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct) => action();
        public void Release() { }
    }

    private sealed class StubLlamaSync : IDaemonLlamaSync
    {
        public Task SyncAsync(WorkstationSettings settings, CancellationToken ct) => Task.CompletedTask;
        public Task RunLockedAsync(Func<Task> action, CancellationToken ct) => action();
        public Task ReloadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UnloadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SyncNowAsync(CancellationToken ct) => Task.CompletedTask;
        public void Release() { }
    }

    private sealed class StubState : IDaemonRuntimeState
    {
        public DaemonStatus Snapshot => new();
        public void Log(string message) { }
        public void Publish(Func<DaemonStatus, DaemonStatus> update) { }
    }
}
