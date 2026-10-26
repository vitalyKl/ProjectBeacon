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

    private sealed record CapturedPost(string Method, string Path, string? ProjectHeader);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<CapturedPost> _posts;

        public CapturingHandler(List<CapturedPost> posts) => _posts = posts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _posts.Add(new CapturedPost(request.Method.Method, request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("X-Project-Id", out var values) ? string.Join(",", values) : null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class StubRuntime : IAgentRuntime
    {
        private int _statusCalls;

        public Task<string> CreateSessionAsync(string title, CancellationToken ct) => Task.FromResult("oc-1");

        public Task SendPromptAsync(string sessionId, string text, string? model, CancellationToken ct, AgentPromptControls? controls = null) => Task.CompletedTask;

        public async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(string sessionId, CancellationToken ct)
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
            => Task.FromResult(new AgentSessionUsage(1, 1, 1, 0));

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
