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
    public void McpChildReceivesExplicitSubtaskScopeFromHarnessAuthorization()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-native-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var taskId = Guid.NewGuid();
        var subtaskId = Guid.NewGuid();
        try
        {
            var authorization = new AgentHarnessAuthorization(
                "https://beacon.test",
                "ephemeral-token",
                Guid.NewGuid(),
                taskId,
                Guid.NewGuid(),
                1,
                root,
                DateTime.UtcNow.AddMinutes(2),
                subtaskId);

            var start = McpStdioClient.CreateStartInfo(root, authorization);

            Assert.Equal(taskId.ToString("D"), start.Environment["BEACON_TASK_ID"]);
            Assert.Equal(subtaskId.ToString("D"), start.Environment["BEACON_SUBTASK_ID"]);
            Assert.Equal("ephemeral-token", start.Environment["BEACON_API_TOKEN"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

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
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
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
            await streamTask.WaitAsync(TimeSpan.FromSeconds(15));

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
    public async Task NativeRuntimeTruncatesOversizedToolResultsBeforePersistingAndStreaming()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-tool-truncation-" + Guid.NewGuid().ToString("N"));
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
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        await using var llama = new ClientLlamaSwap();
        var originalToolResult = new string('x', 20_000);
        var keptCharacters = 16 * 1024;
        string truncationMarker;
        do
        {
            truncationMarker = $"\n\n[Tool result truncated: original length {originalToolResult.Length} characters; showing first {keptCharacters} characters.]";
            keptCharacters = 16 * 1024 - truncationMarker.Length;
        }
        while (truncationMarker.Length + keptCharacters > 16 * 1024);
        truncationMarker = $"\n\n[Tool result truncated: original length {originalToolResult.Length} characters; showing first {keptCharacters} characters.]";
        var expectedToolResult = originalToolResult[..keptCharacters] + truncationMarker;
        var provider = new StubToolProvider(expectedToolResult);
        var mcp = new StubMcpToolClient(originalToolResult);
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
            var toolDelta = Assert.Single(streamed, item => item.Kind == "tool").Delta;
            Assert.StartsWith("read_file: ", toolDelta, StringComparison.Ordinal);
            Assert.Contains("[Tool result truncated: original length 20000 characters;", toolDelta, StringComparison.Ordinal);
            Assert.True(toolDelta.Length < 16 * 1024 + 20);
            Assert.Equal("read_file", mcp.LastToolName);
            Assert.Equal("README.md", mcp.LastArguments!["path"]!.GetValue<string>());
            Assert.Equal(2, provider.Requests.Count);
            Assert.Contains(provider.Requests[0], message =>
                message?["role"]?.GetValue<string>() == "system"
                && message["content"]?.GetValue<string>()?.Contains(
                    "Never state that a tool is unavailable without having called it in the same turn.",
                    StringComparison.Ordinal) == true);
            Assert.Contains(provider.Requests[1], message =>
                message?["role"]?.GetValue<string>() == "tool"
                && message["tool_call_id"]?.GetValue<string>() == "call-1"
                && message["content"]?.GetValue<string>() == expectedToolResult);
            var transcript = await File.ReadAllTextAsync(Path.Combine(temp, ".opencode", "sessions", session + ".json"));
            Assert.Contains("[Tool result truncated: original length 20000 characters;", transcript, StringComparison.Ordinal);
            Assert.DoesNotContain(originalToolResult, transcript, StringComparison.Ordinal);
            Assert.DoesNotContain("never-persist-this-token", transcript, StringComparison.Ordinal);

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

    [Fact]
    public async Task NativeRuntimeRejectsMcpToolThatWasNotAdvertised()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-unadvertised-" + Guid.NewGuid().ToString("N"));
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
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        await using var llama = new ClientLlamaSwap();
        var provider = new UnadvertisedToolProvider();
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
            var session = await runtime.CreateSessionAsync("unadvertised tool test", CancellationToken.None);
            var streamTask = runtime.StreamTextAsync(session, (_, _, _, _, _) => Task.CompletedTask, CancellationToken.None);
            var authorization = new AgentHarnessAuthorization(
                "https://beacon.test", "never-persist-this-token", Guid.NewGuid(), null,
                Guid.NewGuid(), 1, project, DateTime.UtcNow.AddMinutes(2));
            await runtime.SendPromptAsync(
                session, "Call a hidden tool", "beacon-local/test-model", CancellationToken.None,
                new AgentPromptControls(null, "medium", null) { HarnessAuthorization = authorization });
            await streamTask.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(0, mcp.CallCount);
            Assert.Contains(provider.RequestMessages[1], message =>
                message?["role"]?.GetValue<string>() == "tool"
                && message["content"]?.GetValue<string>()?.Contains("was not advertised", StringComparison.Ordinal) == true);
            var status = await runtime.GetTurnStatusAsync(session, CancellationToken.None);
            Assert.True(status!.Ended);
            Assert.Equal("stop", status.Finish);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public async Task NativeRuntimeStopsRepeatedIdenticalToolCalls()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-loop-" + Guid.NewGuid().ToString("N"));
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
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        await using var llama = new ClientLlamaSwap();
        var provider = new RepeatingToolProvider();
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
            var session = await runtime.CreateSessionAsync("loop test", CancellationToken.None);
            var streamTask = runtime.StreamTextAsync(session, (_, _, _, _, _) => Task.CompletedTask, CancellationToken.None);
            var authorization = new AgentHarnessAuthorization(
                "https://beacon.test", "never-persist-this-token", Guid.NewGuid(), null,
                Guid.NewGuid(), 1, project, DateTime.UtcNow.AddMinutes(2));
            await runtime.SendPromptAsync(
                session, "Read README.md", "beacon-local/test-model", CancellationToken.None,
                new AgentPromptControls(null, "medium", null) { HarnessAuthorization = authorization });
            await streamTask.WaitAsync(TimeSpan.FromSeconds(3));

            var status = await runtime.GetTurnStatusAsync(session, CancellationToken.None);
            Assert.True(status!.Ended);
            Assert.Equal("error", status.Finish);
            Assert.Contains("repeated the same tool call", status.Error);
            Assert.Equal(3, provider.RequestCount);
            Assert.Equal(1, mcp.CallCount);
            Assert.Contains(provider.RequestMessages[2], message =>
                message?["role"]?.GetValue<string>() == "tool"
                && message["content"]?.GetValue<string>()?.Contains("Do not repeat it again", StringComparison.Ordinal) == true);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(temp, true);
        }
    }


    [Fact]
    public async Task NativeRuntimeCompactsOldConversationWhenContextBudgetIsExceeded()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-context-" + Guid.NewGuid().ToString("N"));
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
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        await using var llama = new ClientLlamaSwap();
        var provider = new ContextRecordingProvider();
        var runtime = new NativeAgentRuntime(
            control,
            llama,
            new StubLlamaSync(),
            () => WorkstationSettings.Load(settingsPath),
            providerFactory: (_, _, _) => provider);
        try
        {
            var session = await runtime.CreateSessionAsync("context test", CancellationToken.None);
            for (var i = 1; i <= 4; i++)
            {
                var streamTask = runtime.StreamTextAsync(session, (_, _, _, _, _) => Task.CompletedTask, CancellationToken.None);
                await runtime.SendPromptAsync(
                    session,
                    $"User message {i}",
                    "beacon-local/test-model",
                    CancellationToken.None,
                    new AgentPromptControls(null, "medium", null) { ContextLimit = 1 });
                await streamTask.WaitAsync(TimeSpan.FromSeconds(3));
            }

            Assert.True(provider.SummaryCalls > 0);
            var transcript = await File.ReadAllTextAsync(Path.Combine(temp, ".opencode", "sessions", session + ".json"));
            Assert.Contains("Conversation summary (preserve this as context)", transcript, StringComparison.Ordinal);
            Assert.Contains("User message 4", transcript, StringComparison.Ordinal);
            var status = await runtime.GetTurnStatusAsync(session, CancellationToken.None);
            Assert.True(status!.Ended);
            Assert.Equal("stop", status.Finish);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public async Task NativeRuntimeAbortCancelsProviderAndEndsTurnAsAborted()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-abort-" + Guid.NewGuid().ToString("N"));
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
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        await using var llama = new ClientLlamaSwap();
        var provider = new BlockingProvider();
        var providerCalls = 0;
        var runtime = new NativeAgentRuntime(
            control,
            llama,
            new StubLlamaSync(),
            () => WorkstationSettings.Load(settingsPath),
            providerFactory: (_, _, _) => Interlocked.Increment(ref providerCalls) == 1
                ? provider
                : new StubProvider());
        try
        {
            var session = await runtime.CreateSessionAsync("abort test", CancellationToken.None);
            var streamTask = runtime.StreamTextAsync(session, (_, _, _, _, _) => Task.CompletedTask, CancellationToken.None);
            await runtime.SendPromptAsync(
                session,
                "Start a long response",
                "beacon-local/test-model",
                CancellationToken.None,
                new AgentPromptControls(null, "medium", null));

            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await runtime.AbortAsync(session, CancellationToken.None);

            var status = await runtime.GetTurnStatusAsync(session, CancellationToken.None);
            Assert.True(status!.Ended);
            Assert.Equal("aborted", status.Finish);
            Assert.True(provider.CancellationObserved);
            Assert.Null(status.Error);
            await streamTask.WaitAsync(TimeSpan.FromSeconds(3));

            var secondStream = new List<string>();
            var secondStreamTask = runtime.StreamTextAsync(session, (_, _, _, delta, _) =>
            {
                secondStream.Add(delta);
                return Task.CompletedTask;
            }, CancellationToken.None);
            await runtime.SendPromptAsync(
                session,
                "Start a fresh turn after abort",
                "beacon-local/test-model",
                CancellationToken.None);
            await secondStreamTask.WaitAsync(TimeSpan.FromSeconds(3));

            var secondStatus = await runtime.GetTurnStatusAsync(session, CancellationToken.None);
            Assert.True(secondStatus!.Ended);
            Assert.Equal("stop", secondStatus.Finish);
            Assert.Equal("Hello, world.", string.Concat(secondStream));
            Assert.Equal(2, providerCalls);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(temp, true);
        }
    }

    private sealed class BlockingProvider : IChatProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Started.TrySetResult();
            yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "Working...");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class RepeatingToolProvider : IChatProvider
    {
        public int RequestCount { get; private set; }
        public List<JsonArray> RequestMessages { get; } = [];

        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequestCount++;
            RequestMessages.Add((JsonArray)request.Messages.DeepClone());
            yield return new ChatProviderEvent(
                ChatProviderEventKind.ToolCallDelta,
                ToolIndex: 0,
                ToolCallId: $"call-{RequestCount}",
                ToolName: "read_file");
            yield return new ChatProviderEvent(
                ChatProviderEventKind.ToolCallDelta,
                ToolIndex: 0,
                ToolArguments: "{\"path\":\"README.md\"}");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "tool_calls");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
            await Task.CompletedTask;
        }
    }

    private sealed class UnadvertisedToolProvider : IChatProvider
    {
        public List<JsonArray> RequestMessages { get; } = [];
        private int _calls;

        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            RequestMessages.Add((JsonArray)request.Messages.DeepClone());
            if (Interlocked.Increment(ref _calls) == 1)
            {
                yield return new ChatProviderEvent(
                    ChatProviderEventKind.ToolCallDelta,
                    ToolIndex: 0,
                    ToolCallId: "hidden-call",
                    ToolName: "delete_everything",
                    ToolArguments: "{}");
                yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "tool_calls");
                yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
                yield break;
            }

            yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "The hidden tool was blocked.");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "stop");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
            await Task.CompletedTask;
        }
    }

    private sealed class ContextRecordingProvider : IChatProvider
    {
        public int SummaryCalls { get; private set; }

        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var isSummary = request.Messages.Any(message =>
                message?["content"]?.GetValue<string>()?.Contains("Summarize the conversation history", StringComparison.Ordinal) == true);
            if (isSummary)
            {
                SummaryCalls++;
                yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "Preserve earlier user goals and decisions.");
            }
            else
            {
                yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "Context test response.");
                yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "stop");
            }

            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
            await Task.CompletedTask;
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

    private sealed class StubToolProvider(string expectedToolContent = "README contents") : IChatProvider
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
                && message["content"]?.GetValue<string>() == expectedToolContent);
            yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "Read complete.");
            yield return new ChatProviderEvent(ChatProviderEventKind.Usage, PromptTokens: 7, CompletionTokens: 2);
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "stop");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
        }
    }

    private sealed class StubMcpToolClient(string resultText = "README contents") : IMcpToolClient
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
            return Task.FromResult(new McpToolResult(false, resultText));
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
