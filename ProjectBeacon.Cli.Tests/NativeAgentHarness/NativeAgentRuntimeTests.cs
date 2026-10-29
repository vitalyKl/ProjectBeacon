namespace ProjectBeacon.Cli.Tests.NativeAgentHarness;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public async Task NativeRuntimeExecutesFragmentedModelToolCallAndFeedsResultBackToModel()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-tools-" + Guid.NewGuid().ToString("N"));
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
        var provider = new StubToolProvider();
        var mcp = new StubMcpToolClient();
        var runtime = new NativeAgentRuntime(
            control,
            llama,
            new StubLlamaSync(),
            () => WorkstationSettings.Load(settingsPath),
            providerFactory: (_, _, _) => provider,
            mcpClientFactory: (_, _) => Task.FromResult<IMcpToolClient>(mcp));
        try
        {
            var session = await runtime.CreateSessionAsync("tool test", CancellationToken.None);
            var streamed = new List<(string Kind, string Delta)>();
            var streamTask = runtime.StreamTextAsync(session, (kind, _, _, delta, _) =>
            {
                streamed.Add((kind, delta));
                return Task.CompletedTask;
            }, CancellationToken.None);

            var authorization = new AgentHarnessAuthorization(
                "https://beacon.test",
                "never-persist-this-token",
                Guid.NewGuid(),
                null,
                Guid.NewGuid(),
                1,
                project,
                DateTime.UtcNow.AddMinutes(2));
            await runtime.SendPromptAsync(
                session,
                "Read README.md then summarize it",
                "beacon-local/test-model",
                CancellationToken.None,
                new AgentPromptControls(null, "medium", null) { HarnessAuthorization = authorization });
            await streamTask.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal("Read complete.", string.Concat(streamed.Where(item => item.Kind == "text").Select(item => item.Delta)));
            Assert.Contains(streamed, item => item.Kind == "tool" && item.Delta == "read_file: README contents");
            Assert.Equal("read_file", mcp.LastToolName);
            Assert.Equal("README.md", mcp.LastArguments!["path"]!.GetValue<string>());
            Assert.Equal(2, provider.Requests.Count);
            Assert.Contains(provider.Requests[1], message =>
                message?["role"]?.GetValue<string>() == "tool"
                && message["tool_call_id"]?.GetValue<string>() == "call-1"
                && message["content"]?.GetValue<string>() == "README contents");
            Assert.DoesNotContain("never-persist-this-token", await File.ReadAllTextAsync(
                Path.Combine(temp, ".opencode", "sessions", session + ".json")));

            var status = await runtime.GetTurnStatusAsync(session, CancellationToken.None);
            Assert.True(status!.Ended);
            Assert.Equal("stop", status.Finish);
            Assert.Equal(1, mcp.CallCount);
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

    private sealed class StubToolProvider : IChatProvider
    {
        private int _calls;
        public List<JsonArray> Requests { get; } = [];

        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add((JsonArray)request.Messages.DeepClone());
            if (Interlocked.Increment(ref _calls) == 1)
            {
                yield return new ChatProviderEvent(ChatProviderEventKind.ToolCallDelta, ToolIndex: 0, ToolCallId: "call-");
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return new ChatProviderEvent(ChatProviderEventKind.ToolCallDelta, ToolIndex: 0, ToolCallId: "1", ToolName: "read_");
                yield return new ChatProviderEvent(ChatProviderEventKind.ToolCallDelta, ToolIndex: 0, ToolName: "file", ToolArguments: "{\"path\":");
                yield return new ChatProviderEvent(ChatProviderEventKind.ToolCallDelta, ToolIndex: 0, ToolArguments: "\"README.md\"}");
                yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "tool_calls");
                yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
                yield break;
            }

            Assert.Contains(request.Messages, message =>
                message?["role"]?.GetValue<string>() == "tool"
                && message["content"]?.GetValue<string>() == "README contents");
            yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "Read complete.");
            yield return new ChatProviderEvent(ChatProviderEventKind.Usage, PromptTokens: 7, CompletionTokens: 2);
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "stop");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
        }
    }

    private sealed class StubMcpToolClient : IMcpToolClient
    {
        public JsonArray OpenAiTools { get; } = new()
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "read_file",
                    ["description"] = "Read a file inside the project root.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray { JsonValue.Create("path") }
                    }
                }
            }
        };

        public string? LastToolName { get; private set; }
        public JsonObject? LastArguments { get; private set; }
        public int CallCount { get; private set; }

        public Task<McpToolResult> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastToolName = name;
            LastArguments = (JsonObject)arguments.DeepClone();
            CallCount++;
            return Task.FromResult(new McpToolResult(false, "README contents"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
