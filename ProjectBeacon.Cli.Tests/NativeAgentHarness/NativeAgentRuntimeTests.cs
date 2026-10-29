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
        var runtime = new NativeAgentRuntime(control, llama, new StubLlamaSync(), () => WorkstationSettings.Load(settingsPath));
        try
        {
            var session = await runtime.CreateSessionAsync("test", CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(temp, "sessions", session + ".json")));
            var ready = await runtime.EnsureReadyAsync(project, CancellationToken.None);
            Assert.True(ready.Ready);

            var status = await runtime.GetTurnStatusAsync(session, CancellationToken.None);
            Assert.True(status!.Ended);
            var parts = new List<AgentMessagePart>();
            await foreach (var part in runtime.StreamPartsAsync(session, CancellationToken.None))
                parts.Add(part);
            Assert.Empty(parts);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(temp, true);
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
