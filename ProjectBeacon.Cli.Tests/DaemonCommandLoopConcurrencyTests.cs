namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Text.Json;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Domain.Enums;

public sealed class DaemonCommandLoopConcurrencyTests
{
    [Fact]
    public async Task MultiplePromptCommands_AreDispatchedWithoutWaitingForEarlierSessions()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var api = new TwoPromptApi(firstId, secondId);
        using var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var executor = new BlockingPromptExecutor();
        var state = new DaemonRuntimeState("http://localhost", null);
        var timing = new DaemonTiming { DelayAsync = (_, ct) => Task.Delay(1, ct) };
        var deps = new DaemonDependencies(http, null!, null!, null!, () => new WorkstationSettings());
        var loop = new DaemonCommandLoop(deps, executor, state, timing);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = loop.RunAsync(cts.Token);

        await executor.BothPromptsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        executor.Release.TrySetResult(true);
        await api.BothCommandsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, executor.Started);
        Assert.Equal(2, api.Completed);
    }

    private sealed class BlockingPromptExecutor : IDaemonCommandExecutor
    {
        private int _started;

        public int Started => Volatile.Read(ref _started);
        public TaskCompletionSource<bool> BothPromptsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(
            WorkstationDaemon.CommandWire command, CancellationToken ct)
        {
            if (command.Kind != WorkstationCommandKind.ChatPrompt)
                return (false, null, $"Unexpected command kind {command.Kind}.");

            if (Interlocked.Increment(ref _started) == 2)
                BothPromptsStarted.TrySetResult(true);

            await Release.Task.WaitAsync(ct);
            return (true, "{}", null);
        }
    }

    private sealed class TwoPromptApi(Guid firstId, Guid secondId) : HttpMessageHandler
    {
        private int _normalClaims;
        private int _completed;

        public int Completed => Volatile.Read(ref _completed);
        public TaskCompletionSource<bool> BothCommandsCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;

            if (path == "/v1/devices/me/commands")
            {
                if (uri.Query.Contains("controlOnly=true", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.NoContent);

                var claim = Interlocked.Increment(ref _normalClaims);
                if (claim == 1)
                    return JsonResponse(Command(firstId));
                if (claim == 2)
                    return JsonResponse(Command(secondId));

                await Task.Delay(5, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.EndsWith("/complete", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _completed) == 2)
                    BothCommandsCompleted.TrySetResult(true);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static object Command(Guid id) => new
        {
            id,
            kind = WorkstationCommandKind.ChatPrompt.ToString(),
            version = 1,
            generation = 1,
            payloadJson = "{}"
        };

        private static HttpResponseMessage JsonResponse(object body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
            };
    }
}
