namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Text.Json;
using System.Collections.Concurrent;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Domain.Enums;

public sealed class DaemonCommandLoopTests
{
    [Fact]
    public async Task Complete_RetriesAfter500_ExecutesExactlyOnce()
    {
        var commandId = Guid.NewGuid();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (loop, executor, _, api) = BuildLoop(commandId, attempt =>
            attempt == 0
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        api.OnCompleteRequest = n =>
        {
            if (n == 2) done.TrySetResult(true);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var run = loop.RunAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal(1, executor.Executed);
        Assert.Equal(2, api.CompleteRequests);
        foreach (var body in api.CompleteBodies)
        {
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(1, doc.RootElement.GetProperty("generation").GetInt32());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        }
    }

    [Fact]
    public async Task Complete_DoesNotRetryOn4xx()
    {
        var commandId = Guid.NewGuid();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (loop, executor, state, api) = BuildLoop(commandId,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        api.OnCompleteRequest = _ => done.TrySetResult(true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var run = loop.RunAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        // The fake signals when the HTTP request is sent, before the command loop has
        // received the 4xx response and emitted its terminal (non-retry) log entry.
        // Wait for that observable outcome before cancelling the loop; otherwise slower
        // runners can cancel between the callback and response processing.
        AssertLogEventually(state, "not retrying");
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal(1, executor.Executed);
        Assert.Equal(1, api.CompleteRequests);
    }

    [Fact]
    public async Task Complete_TransportFailure_RetriesThenSucceeds()
    {
        var commandId = Guid.NewGuid();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (loop, executor, state, api) = BuildLoop(commandId, attempt =>
            attempt == 0
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        api.OnCompleteRequest = n =>
        {
            if (n == 2) done.TrySetResult(true);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var run = loop.RunAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal(1, executor.Executed);
        Assert.Equal(2, api.CompleteRequests);
        // The fake signals when the retry request is sent, before the loop has written
        // its "retrying" verdict; poll like the other retry tests.
        AssertLogEventually(state, "retrying");
    }

    [Fact]
    public async Task Complete_RetryExhaustion_GivesUpWithoutReexecuting()
    {
        var commandId = Guid.NewGuid();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (loop, executor, state, api) = BuildLoop(commandId,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        api.OnCompleteRequest = n =>
        {
            if (n == 5) done.TrySetResult(true);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var run = loop.RunAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal(1, executor.Executed);
        Assert.Equal(5, api.CompleteRequests);
        AssertLogEventually(state, "failed after 5 attempts");
    }

    [Fact]
    public async Task ChatPrompt_ControlLaneExecutesAbortBeforePromptCompletes()
    {
        var promptStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ControlLaneApi(Guid.NewGuid(), Guid.NewGuid(), promptStarted, abortHandled);
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var executor = new ControlLaneExecutor(promptStarted, abortHandled);
        var state = new DaemonRuntimeState("http://localhost", null);
        var timing = new DaemonTiming { DelayAsync = (_, ct) => Task.CompletedTask };
        var deps = new DaemonDependencies(http, null!, null!, null!, () => new WorkstationSettings());
        var loop = new DaemonCommandLoop(deps, executor, state, timing);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var run = loop.RunAsync(cts.Token);
        await api.BothCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal(1, executor.PromptExecutions);
        Assert.Equal(1, executor.AbortExecutions);
        Assert.Contains(api.RequestPaths, path => path.Contains("controlOnly=true", StringComparison.Ordinal));
        http.Dispose();
    }

    [Fact]
    public async Task Heartbeat_IncludesInstanceId()
    {
        var api = new FakeApi(Guid.NewGuid(), _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var tempDir = Path.Combine(Path.GetTempPath(), "beacon-hb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        await using var llama = new ClientLlamaSwap { SkipRealProcess = true, ConfigFile = Path.Combine(tempDir, "config.yaml") };
        await using var openCode = new ClientOpenCodeServe { SkipRealProcess = true };
        var state = new DaemonRuntimeState("http://localhost", null);
        var timing = new DaemonTiming { DelayAsync = (_, ct) => Task.CompletedTask };
        var deps = new DaemonDependencies(http, llama, openCode, null!, () => new WorkstationSettings());
        var loop = new DaemonHeartbeatLoop(deps, new DaemonLlamaSync(deps, state), new DaemonOpenCodeGate(deps), state, timing);

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnHeartbeat = () => done.TrySetResult(true);

        // The first heartbeat iteration launches up to eleven bounded `where`/`which`
        // processes (WorkstationProbe.Which, 5 s each), so on a loaded CI host the first
        // beat can take well over 10 s even though nothing is broken. Wait past the
        // ~55 s worst case.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var run = loop.RunAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        http.Dispose();

        using var doc = JsonDocument.Parse(api.HeartbeatBody!);
        Assert.Equal(DaemonInstance.Id, doc.RootElement.GetProperty("instanceId").GetString());
    }

    /// <summary>
    /// The fake signals completion a few thread-pool hops before the loop writes its
    /// verdict log line; loaded CI hosts have starved the continuation past 5 s, so poll
    /// long enough to stay under the test's cancellation guard.
    /// </summary>
    private static void AssertLogEventually(DaemonRuntimeState state, string fragment)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !state.Snapshot.Log.Any(l => l.Contains(fragment, StringComparison.Ordinal)))
            Thread.Sleep(10);
        Assert.Contains(state.Snapshot.Log, l => l.Contains(fragment, StringComparison.Ordinal));
    }

    private static (DaemonCommandLoop Loop, CountingExecutor Executor, DaemonRuntimeState State, FakeApi Api)
        BuildLoop(Guid commandId, Func<int, Task<HttpResponseMessage>> completeScript)
    {
        var api = new FakeApi(commandId, completeScript);
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var executor = new CountingExecutor();
        var state = new DaemonRuntimeState("http://localhost", null);
        var timing = new DaemonTiming { DelayAsync = (_, ct) => Task.CompletedTask };
        var deps = new DaemonDependencies(http, null!, null!, null!, () => new WorkstationSettings());
        return (new DaemonCommandLoop(deps, executor, state, timing), executor, state, api);
    }

    private sealed class ControlLaneExecutor(
        TaskCompletionSource<bool> promptStarted,
        TaskCompletionSource<bool> abortHandled) : IDaemonCommandExecutor
    {
        private int _promptExecutions;
        private int _abortExecutions;
        public int PromptExecutions => Volatile.Read(ref _promptExecutions);
        public int AbortExecutions => Volatile.Read(ref _abortExecutions);

        public async Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(
            WorkstationDaemon.CommandWire command, CancellationToken ct)
        {
            if (command.Kind == WorkstationCommandKind.ChatPrompt)
            {
                Interlocked.Increment(ref _promptExecutions);
                promptStarted.TrySetResult(true);
                await abortHandled.Task.WaitAsync(ct);
                return (true, "{}", null);
            }

            if (command.Kind == WorkstationCommandKind.ChatAbort)
            {
                Interlocked.Increment(ref _abortExecutions);
                abortHandled.TrySetResult(true);
                return (true, "{}", null);
            }

            return (false, null, $"Unexpected command {command.Kind}.");
        }
    }

    private sealed class ControlLaneApi(
        Guid promptId,
        Guid abortId,
        TaskCompletionSource<bool> promptStarted,
        TaskCompletionSource<bool> abortHandled) : HttpMessageHandler
    {
        private int _normalClaimed;
        private int _controlClaimed;
        private int _completions;
        public ConcurrentQueue<string> RequestPaths { get; } = new();
        public TaskCompletionSource<bool> BothCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            RequestPaths.Enqueue(uri.PathAndQuery);

            if (path == "/v1/devices/me/commands")
            {
                if (uri.Query.Contains("controlOnly=true", StringComparison.Ordinal))
                {
                    if (Interlocked.CompareExchange(ref _controlClaimed, 1, 0) != 0)
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    return JsonResponse(new
                    {
                        id = abortId,
                        kind = "ChatAbort",
                        version = 1,
                        generation = 1,
                        payloadJson = "{}"
                    });
                }

                if (Interlocked.CompareExchange(ref _normalClaimed, 1, 0) == 0)
                {
                    return JsonResponse(new
                    {
                        id = promptId,
                        kind = "ChatPrompt",
                        version = 1,
                        generation = 1,
                        payloadJson = "{}"
                    });
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.EndsWith("/complete", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _completions) == 2)
                    BothCompleted.TrySetResult(true);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage JsonResponse(object body)
        {
            var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class CountingExecutor : IDaemonCommandExecutor
    {
        public int Executed;

        public Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(WorkstationDaemon.CommandWire command, CancellationToken ct)
        {
            Interlocked.Increment(ref Executed);
            (bool Ok, string? Result, string? Error) value = (true, "{}", null);
            return Task.FromResult(value);
        }
    }

    private sealed class FakeApi : System.Net.Http.HttpMessageHandler
    {
        private readonly Guid _commandId;
        private readonly Func<int, Task<HttpResponseMessage>> _completeScript;
        private int _completeCount;

        public FakeApi(Guid commandId, Func<int, Task<HttpResponseMessage>> completeScript)
        {
            _commandId = commandId;
            _completeScript = completeScript;
        }

        public int CompleteRequests => Volatile.Read(ref _completeCount);
        public int ClaimRequests;
        public readonly List<string> CompleteBodies = new();
        public string? HeartbeatBody;
        public Action<int>? OnCompleteRequest;
        public Action? OnHeartbeat;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Yield like real network I/O: a completed-task fake lets the loop
            // spin synchronously on the calling thread until the CTS lifetime fires.
            await Task.Delay(1, cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/complete", StringComparison.Ordinal))
            {
                var index = Interlocked.Increment(ref _completeCount) - 1;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                CompleteBodies.Add(body);
                OnCompleteRequest?.Invoke(index + 1);
                return await _completeScript(index);
            }
            if (path.Contains("/heartbeat", StringComparison.Ordinal))
            {
                HeartbeatBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                OnHeartbeat?.Invoke();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (path == "/v1/devices/me/commands")
            {
                var n = Interlocked.Increment(ref ClaimRequests);
                if (n == 1)
                    return JsonResponse(new
                    {
                        id = _commandId,
                        kind = "Probe",
                        version = 1,
                        generation = 1,
                        payloadJson = "{}"
                    });
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage JsonResponse(object body)
        {
            var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
