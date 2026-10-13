namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Text.Json;
using ProjectBeacon.Cli.Client;

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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = loop.RunAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = loop.RunAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal(1, executor.Executed);
        Assert.Equal(1, api.CompleteRequests);
        Assert.Contains(state.Snapshot.Log, line => line.Contains("not retrying"));
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = loop.RunAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal(1, executor.Executed);
        Assert.Equal(2, api.CompleteRequests);
        Assert.Contains(state.Snapshot.Log, line => line.Contains("retrying"));
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = loop.RunAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal(1, executor.Executed);
        Assert.Equal(5, api.CompleteRequests);
        Assert.Contains(state.Snapshot.Log, line => line.Contains("failed after 5 attempts"));
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = loop.RunAsync(cts.Token);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        http.Dispose();

        using var doc = JsonDocument.Parse(api.HeartbeatBody!);
        Assert.Equal(DaemonInstance.Id, doc.RootElement.GetProperty("instanceId").GetString());
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
