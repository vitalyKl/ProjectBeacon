namespace ProjectBeacon.Cli.Tests.NativeAgentHarness;

using System.Net;
using System.Text;
using System.Text.Json;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class NativeAgentRuntimeTests
{
    [Fact]
    public async Task CreatesSessionPersistsTranscriptStreamsAndReportsTurnStatus()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var project = Path.Combine(temp, "project");
        Directory.CreateDirectory(project);
        var settingsPath = Path.Combine(temp, "workstation.json");
        new WorkstationSettings
        {
            ProjectsRoot = project,
            OpencodeDataDir = Path.Combine(temp, ".opencode", "data"),
            LlamaSwapPort = 8080
        }.Save(settingsPath);

        using var control = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        })) { BaseAddress = new Uri("https://beacon.test/") };
        await using var llama = new ClientLlamaSwap();
        var runtime = new NativeAgentRuntime(
            control,
            llama,
            new StubLlamaSync(),
            () => WorkstationSettings.Load(settingsPath),
            providerFactory: (_, _, _) => new StubProvider());
        try
        {
            var session = await runtime.CreateSessionAsync("test", CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(temp, ".opencode", "sessions", session + ".json")));
            var ready = await runtime.EnsureReadyAsync(project, CancellationToken.None);
            Assert.True(ready.Ready);

            var streamed = new List<string>();
            var streamTask = runtime.StreamTextAsync(session, (_, _, _, delta, _) =>
            {
                streamed.Add(delta);
                return Task.CompletedTask;
            }, CancellationToken.None);
            await runtime.SendPromptAsync(
                session,
                "say hi",
                "beacon-local/test-model",
                CancellationToken.None,
                new AgentPromptControls(null, "medium", null) { ContextLimit = 32768 });
            await streamTask.WaitAsync(TimeSpan.FromSeconds(3));

            var status = await runtime.GetTurnStatusAsync(session, CancellationToken.None);
            Assert.True(status!.Ended);
            Assert.Equal("stop", status.Finish);
            Assert.Equal("Hello, world.", string.Concat(streamed));
            Assert.Equal(12, (await runtime.ReadUsageAsync(session, CancellationToken.None)).PromptTokens);
            Assert.Equal(32768, await runtime.ReadContextLimitAsync("beacon-local/test-model", CancellationToken.None));
            var parts = new List<AgentMessagePart>();
            await foreach (var part in runtime.StreamPartsAsync(session, CancellationToken.None))
                parts.Add(part);
            Assert.Contains(parts, part => part.Kind == "text" && part.Body == "Hello, world." && part.Complete);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(temp, true);
        }
    }

    private sealed class StubProvider : IChatProvider
    {
        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "Hello, ");
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "world.");
            yield return new ChatProviderEvent(ChatProviderEventKind.Usage, PromptTokens: 12, CompletionTokens: 3);
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "stop");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
        }
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
