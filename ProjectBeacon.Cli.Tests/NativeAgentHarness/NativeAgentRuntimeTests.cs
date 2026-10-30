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
    [Theory]
    [InlineData("Planner", true, false)]
    [InlineData("Review", true, false)]
    [InlineData("Actor", false, false)]
    [InlineData("Actor", true, true)]
    public void RunCommandRequiresActorRoleAndExplicitSubtaskGrant(string role, bool grantRunCommand, bool expected)
    {
        var authorization = new AgentHarnessAuthorization(
            "https://beacon.test",
            "ephemeral-token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            Path.GetTempPath(),
            DateTime.UtcNow.AddMinutes(2),
            Guid.NewGuid(),
            role,
            grantRunCommand ? ["read_file", "run_command"] : ["read_file"]);

        Assert.Equal(expected, NativeAgentRuntime.IsRunCommandAuthorized(authorization));
    }

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
                subtaskId,
                "Actor");

            var start = McpStdioClient.CreateStartInfo(root, authorization);

            Assert.Equal(taskId.ToString("D"), start.Environment["BEACON_TASK_ID"]);
            Assert.Equal(subtaskId.ToString("D"), start.Environment["BEACON_SUBTASK_ID"]);
            Assert.Equal("Actor", start.Environment["BEACON_PIPELINE_ROLE"]);
            Assert.Equal("ephemeral-token", start.Environment["BEACON_API_TOKEN"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NativeRuntimeReadinessWarnsWhenLocalModelCommandOmitsJinja()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var project = Path.Combine(temp, "project");
        Directory.CreateDirectory(project);
        var config = Path.Combine(temp, "llama-swap.yaml");
        await File.WriteAllTextAsync(config, "models:\\n  qwen:\\n    cmd: \\\"llama-server -m qwen.gguf --ctx-size 8192\\\"\\n");

        using var control = new HttpClient { BaseAddress = new Uri("https://beacon.test/") };
        await using var llama = new ClientLlamaSwap { ConfigFile = config };
        var runtime = new NativeAgentRuntime(control, llama, new StubLlamaSync(), () => new WorkstationSettings());
        try
        {
            var readiness = await runtime.EnsureReadyAsync(project, CancellationToken.None);
            Assert.True(readiness.Ready);
            Assert.Contains("--jinja", readiness.Warning, StringComparison.Ordinal);
            Assert.Contains("qwen", readiness.Warning, StringComparison.Ordinal);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(temp, true);
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
    public async Task NativeRuntimeRejectsSessionIdsThatCouldEscapeTranscriptDirectory()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-session-path-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(temp, ".opencode", "data");
        var outsideTranscript = Path.Combine(temp, ".opencode", "outside.json");
        Directory.CreateDirectory(Path.Combine(temp, ".opencode", "sessions"));
        Directory.CreateDirectory(Path.GetDirectoryName(outsideTranscript)!);
        await File.WriteAllTextAsync(outsideTranscript, new JsonObject
        {
            ["id"] = "../outside",
            ["title"] = "outside",
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["messages"] = new JsonArray(),
            ["parts"] = new JsonArray(),
            ["turnNumber"] = 0,
            ["usage"] = new JsonObject
            {
                ["promptTokens"] = 0,
                ["completionTokens"] = 0,
                ["assistantMessages"] = 0,
                ["totalCost"] = 0,
                ["generationSeconds"] = 0
            },
            ["turnStatus"] = new JsonObject { ["ended"] = true, ["finish"] = "stop" }
        }.ToJsonString());

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
            () => new WorkstationSettings
            {
                OpencodeDataDir = dataDirectory,
                LlamaSwapPort = 8080
            });

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => runtime.GetTurnStatusAsync("../outside", CancellationToken.None));
            Assert.Contains("../outside", await File.ReadAllTextAsync(outsideTranscript), StringComparison.Ordinal);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public async Task NativeRuntimeRecoversUnrecordedToolResultsAfterRestartWithoutReplayingTools()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-tool-recovery-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(temp, ".opencode", "data");
        var sessionsDirectory = Path.Combine(temp, ".opencode", "sessions");
        Directory.CreateDirectory(sessionsDirectory);
        var sessionId = Guid.NewGuid().ToString("N");
        var transcriptPath = Path.Combine(sessionsDirectory, sessionId + ".json");
        var toolCalls = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "call-1",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = new JsonObject { ["path"] = "README.md" }.ToJsonString() }
            },
            new JsonObject
            {
                ["id"] = "call-2",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "run_command", ["arguments"] = new JsonObject { ["command"] = "dotnet test" }.ToJsonString() }
            }
        };
        var transcript = new JsonObject
        {
            ["id"] = sessionId,
            ["title"] = "interrupted session",
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "system prompt" },
                new JsonObject { ["role"] = "user", ["content"] = "inspect and test the project" },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = toolCalls
                },
                new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = "call-1",
                    ["content"] = "README contents"
                }
            },
            ["parts"] = new JsonArray(),
            ["turnNumber"] = 1,
            ["lastModel"] = "beacon-local/test-model",
            ["contextLimit"] = 8192,
            ["usage"] = new JsonObject
            {
                ["promptTokens"] = 10,
                ["completionTokens"] = 4,
                ["assistantMessages"] = 1,
                ["totalCost"] = 0,
                ["generationSeconds"] = 1
            },
            ["turnStatus"] = new JsonObject
            {
                ["ended"] = false,
                ["finish"] = null,
                ["error"] = null,
                ["messageId"] = "native:" + sessionId + ":1"
            }
        };
        await File.WriteAllTextAsync(transcriptPath, transcript.ToJsonString());

        using var control = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        await using var llama = new ClientLlamaSwap();
        var provider = new RequestRecordingProvider();
        var runtime = new NativeAgentRuntime(
            control,
            llama,
            new StubLlamaSync(),
            () => new WorkstationSettings
            {
                OpencodeDataDir = dataDirectory,
                LlamaSwapPort = 8080
            },
            providerFactory: (_, _, _) => provider);

        try
        {
            var recoveredStatus = await runtime.GetTurnStatusAsync(sessionId, CancellationToken.None);
            Assert.True(recoveredStatus!.Ended);
            Assert.Equal("aborted", recoveredStatus.Finish);

            var recoveredTranscript = JsonNode.Parse(await File.ReadAllTextAsync(transcriptPath))!.AsObject();
            Assert.Equal("aborted", recoveredTranscript["turnStatus"]!["finish"]!.GetValue<string>());
            Assert.Single(recoveredTranscript["messages"]!.AsArray(), message =>
                message?["role"]?.GetValue<string>() == "tool"
                && message["tool_call_id"]?.GetValue<string>() == "call-2");

            var streamTask = runtime.StreamTextAsync(sessionId, (_, _, _, _, _) => Task.CompletedTask, CancellationToken.None);
            await runtime.SendPromptAsync(
                sessionId,
                "Continue after the restart",
                "beacon-local/test-model",
                CancellationToken.None);
            await streamTask.WaitAsync(TimeSpan.FromSeconds(5));

            var request = Assert.IsType<JsonArray>(provider.RequestMessages);
            Assert.Contains(request, message =>
                message?["role"]?.GetValue<string>() == "tool"
                && message["tool_call_id"]?.GetValue<string>() == "call-1"
                && message["content"]?.GetValue<string>() == "README contents");
            var recovered = Assert.Single(request, message =>
                message?["role"]?.GetValue<string>() == "tool"
                && message["tool_call_id"]?.GetValue<string>() == "call-2");
            Assert.Contains("may or may not have completed", recovered!["content"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Contains("not replayed automatically", recovered["content"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("Continue after the restart", request.Last()!["content"]!.GetValue<string>());

            var persisted = JsonNode.Parse(await File.ReadAllTextAsync(transcriptPath))!.AsObject();
            Assert.Equal("stop", persisted["turnStatus"]!["finish"]!.GetValue<string>());
            Assert.Single(persisted["messages"]!.AsArray(), message =>
                message?["role"]?.GetValue<string>() == "tool"
                && message["tool_call_id"]?.GetValue<string>() == "call-2");
            Assert.Contains(await CollectRuntimePartsAsync(runtime, sessionId), part =>
                part.ExternalId == $"native-recovery:{sessionId}:call-2" && part.ToolStatus == "error");
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(temp, true);
        }
    }

    private static async Task<List<AgentMessagePart>> CollectRuntimePartsAsync(NativeAgentRuntime runtime, string sessionId)
    {
        var parts = new List<AgentMessagePart>();
        await foreach (var part in runtime.StreamPartsAsync(sessionId, CancellationToken.None))
            parts.Add(part);
        return parts;
    }

    [Fact]
    public async Task NativeRuntimeReleasesTurnReservationWhenInitialTranscriptPersistFails()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-persist-failure-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(temp, ".opencode", "data");
        var sessionsDirectory = Path.Combine(temp, ".opencode", "sessions");
        Directory.CreateDirectory(sessionsDirectory);

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
            () => new WorkstationSettings
            {
                OpencodeDataDir = dataDirectory,
                LlamaSwapPort = 8080
            },
            providerFactory: (_, _, _) => new RequestRecordingProvider());

        try
        {
            var sessionId = await runtime.CreateSessionAsync("persist failure", CancellationToken.None);
            var transcriptPath = Path.Combine(sessionsDirectory, sessionId + ".json");
            File.Delete(transcriptPath);
            Directory.CreateDirectory(transcriptPath);

            var persistException = await Assert.ThrowsAnyAsync<Exception>(() => runtime.SendPromptAsync(
                sessionId,
                "This prompt cannot be persisted yet",
                "beacon-local/test-model",
                CancellationToken.None));
            Assert.True(
                persistException is IOException or UnauthorizedAccessException,
                $"Expected a filesystem write failure, got {persistException.GetType().Name}.");

            Directory.Delete(transcriptPath);
            var streamTask = runtime.StreamTextAsync(sessionId, (_, _, _, _, _) => Task.CompletedTask, CancellationToken.None);
            await runtime.SendPromptAsync(
                sessionId,
                "Retry after fixing storage",
                "beacon-local/test-model",
                CancellationToken.None);
            await streamTask.WaitAsync(TimeSpan.FromSeconds(5));

            var status = await runtime.GetTurnStatusAsync(sessionId, CancellationToken.None);
            Assert.True(status!.Ended);
            Assert.Equal("stop", status.Finish);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(transcriptPath))!.AsObject();
            Assert.Equal(1, saved["turnNumber"]!.GetValue<int>());
            Assert.Contains(saved["messages"]!.AsArray(), message =>
                message?["role"]?.GetValue<string>() == "user"
                && message["content"]?.GetValue<string>() == "Retry after fixing storage");
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
            await streamTask.WaitAsync(TimeSpan.FromSeconds(15));

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
    public async Task NativeRuntimeDoesNotExecuteToolCallsWhenProviderStreamEndsWithoutDone()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-truncated-tool-stream-" + Guid.NewGuid().ToString("N"));
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
        var mcp = new StubMcpToolClient();
        var runtime = new NativeAgentRuntime(
            control,
            llama,
            new StubLlamaSync(),
            () => WorkstationSettings.Load(settingsPath),
            providerFactory: (_, _, _) => new TruncatedToolProvider(),
            mcpClientFactory: (_, _) => Task.FromResult<IMcpToolClient>(mcp));

        try
        {
            var session = await runtime.CreateSessionAsync("truncated stream test", CancellationToken.None);
            var streamTask = runtime.StreamTextAsync(session, (_, _, _, _, _) => Task.CompletedTask, CancellationToken.None);
            var authorization = new AgentHarnessAuthorization(
                "https://beacon.test",
                "ephemeral-token",
                Guid.NewGuid(),
                null,
                Guid.NewGuid(),
                1,
                project,
                DateTime.UtcNow.AddMinutes(2));
            await runtime.SendPromptAsync(
                session,
                "Read README.md",
                "beacon-local/test-model",
                CancellationToken.None,
                new AgentPromptControls(null, "medium", null) { HarnessAuthorization = authorization });
            await streamTask.WaitAsync(TimeSpan.FromSeconds(5));

            var status = await runtime.GetTurnStatusAsync(session, CancellationToken.None);
            Assert.True(status!.Ended);
            Assert.Equal("error", status.Finish);
            Assert.Contains("before [DONE]", status.Error!, StringComparison.Ordinal);
            Assert.Equal(0, mcp.CallCount);
            var transcript = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(temp, ".opencode", "sessions", session + ".json")))!;
            Assert.DoesNotContain(transcript["messages"]!.AsArray(), message =>
                message?["role"]?.GetValue<string>() == "tool");
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
    public async Task NativeRuntimeDoesNotRecoverToolCallsWhileAnotherTurnIsStillExecuting()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-active-tool-call-" + Guid.NewGuid().ToString("N"));
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
        var provider = new StubToolProvider();
        var mcp = new BlockingMcpToolClient();
        var runtime = new NativeAgentRuntime(
            control,
            llama,
            new StubLlamaSync(),
            () => WorkstationSettings.Load(settingsPath),
            providerFactory: (_, _, _) => provider,
            mcpClientFactory: (_, _) => Task.FromResult<IMcpToolClient>(mcp));

        try
        {
            var sessionId = await runtime.CreateSessionAsync("concurrent prompt test", CancellationToken.None);
            var streamTask = runtime.StreamTextAsync(sessionId, (_, _, _, _, _) => Task.CompletedTask, CancellationToken.None);
            var authorization = new AgentHarnessAuthorization(
                "https://beacon.test",
                "ephemeral-token",
                Guid.NewGuid(),
                null,
                Guid.NewGuid(),
                1,
                project,
                DateTime.UtcNow.AddMinutes(2));
            await runtime.SendPromptAsync(
                sessionId,
                "Read README.md",
                "beacon-local/test-model",
                CancellationToken.None,
                new AgentPromptControls(null, "medium", null) { HarnessAuthorization = authorization });

            await mcp.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var transcriptPath = Path.Combine(temp, ".opencode", "sessions", sessionId + ".json");
            var duringToolCall = JsonNode.Parse(await File.ReadAllTextAsync(transcriptPath))!.AsObject();
            Assert.DoesNotContain(duringToolCall["messages"]!.AsArray(), message =>
                message?["role"]?.GetValue<string>() == "tool");

            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SendPromptAsync(
                sessionId,
                "Do not start this concurrent prompt",
                "beacon-local/test-model",
                CancellationToken.None,
                new AgentPromptControls(null, "medium", null) { HarnessAuthorization = authorization }));

            var afterRejectedPrompt = JsonNode.Parse(await File.ReadAllTextAsync(transcriptPath))!.AsObject();
            Assert.DoesNotContain(afterRejectedPrompt["messages"]!.AsArray(), message =>
                message?["role"]?.GetValue<string>() == "tool");
            Assert.DoesNotContain(afterRejectedPrompt["messages"]!.AsArray(), message =>
                message?["role"]?.GetValue<string>() == "user"
                && message["content"]?.GetValue<string>() == "Do not start this concurrent prompt");

            mcp.Release.TrySetResult(true);
            await streamTask.WaitAsync(TimeSpan.FromSeconds(5));
            var status = await runtime.GetTurnStatusAsync(sessionId, CancellationToken.None);
            Assert.True(status!.Ended);
            Assert.Equal("stop", status.Finish);
            Assert.Equal(2, provider.Requests.Count);
        }
        finally
        {
            mcp.Release.TrySetResult(true);
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
    public void ContextCompactionKeepsToolCallAndResultsTogether()
    {
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = "system" },
            new() { ["role"] = "user", ["content"] = "request" },
            new()
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject { ["id"] = "call-1" },
                    new JsonObject { ["id"] = "call-2" }
                }
            },
            new() { ["role"] = "tool", ["tool_call_id"] = "call-1", ["content"] = "first" },
            new() { ["role"] = "tool", ["tool_call_id"] = "call-2", ["content"] = "second" },
            new() { ["role"] = "user", ["content"] = "follow-up" }
        };

        Assert.Equal(5, NativeAgentRuntime.FindSafeContextSplitIndex(messages, 3));
        Assert.Equal(2, NativeAgentRuntime.FindSafeContextSplitIndex(messages, 2));
        Assert.Equal(5, NativeAgentRuntime.FindSafeContextSplitIndex(messages, 5));
    }

    [Fact]
    public void ContextCompactionKeepsIncompleteToolCallGroupOnTheSameSideOfBoundary()
    {
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = "system" },
            new() { ["role"] = "user", ["content"] = "request" },
            new()
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject { ["id"] = "call-1" },
                    new JsonObject { ["id"] = "call-2" }
                }
            },
            new() { ["role"] = "tool", ["tool_call_id"] = "call-1", ["content"] = "first" },
            new() { ["role"] = "user", ["content"] = "interrupted" }
        };

        Assert.Equal(2, NativeAgentRuntime.FindSafeContextSplitIndex(messages, 3));
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
    public async Task NativeRuntimeDoesNotDiscardHistoryWhenContextSummaryIsEmpty()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-native-empty-summary-" + Guid.NewGuid().ToString("N"));
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
        var provider = new EmptySummaryProvider();
        var runtime = new NativeAgentRuntime(
            control,
            llama,
            new StubLlamaSync(),
            () => WorkstationSettings.Load(settingsPath),
            providerFactory: (_, _, _) => provider);

        try
        {
            var session = await runtime.CreateSessionAsync("empty summary test", CancellationToken.None);
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

                var status = await runtime.GetTurnStatusAsync(session, CancellationToken.None);
                Assert.True(status!.Ended);
                if (i < 4)
                    Assert.Equal("stop", status.Finish);
                else
                {
                    Assert.Equal("error", status.Finish);
                    Assert.Contains("empty summary", status.Error!, StringComparison.Ordinal);
                }
            }

            Assert.Equal(1, provider.SummaryCalls);
            var transcriptPath = Path.Combine(temp, ".opencode", "sessions", session + ".json");
            var transcript = JsonNode.Parse(await File.ReadAllTextAsync(transcriptPath))!.AsObject();
            var messages = transcript["messages"]!.AsArray();
            foreach (var i in Enumerable.Range(1, 4))
                Assert.Contains(messages, message =>
                    message?["role"]?.GetValue<string>() == "user"
                    && message["content"]?.GetValue<string>() == $"User message {i}");
            Assert.DoesNotContain(messages, message =>
                message?["content"]?.GetValue<string>()?.Contains(
                    "Conversation summary (preserve this as context)", StringComparison.Ordinal) == true);
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
            yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "Working...");
            try
            {
                // Signal only after the iterator resumed past the first yield and entered
                // the cancellation handler; otherwise the test can cancel before the
                // provider observes the token due to scheduler timing.
                Started.TrySetResult();
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

    private sealed class TruncatedToolProvider : IChatProvider
    {
        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ChatProviderEvent(
                ChatProviderEventKind.ToolCallDelta,
                ToolIndex: 0,
                ToolCallId: "call-truncated",
                ToolName: "read_file",
                ToolArguments: "{\"path\":\"README.md\"}");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "tool_calls");
            throw new ChatProviderException("OpenAI-compatible stream ended before [DONE].", (int)HttpStatusCode.BadGateway, retryable: true);
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

    private sealed class EmptySummaryProvider : IChatProvider
    {
        public int SummaryCalls { get; private set; }

        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var isSummary = request.Messages.Any(message =>
                message?["content"]?.GetValue<string>()?.Contains(
                    "Summarize the conversation history", StringComparison.Ordinal) == true);
            if (isSummary)
            {
                SummaryCalls++;
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

    private sealed class RequestRecordingProvider : IChatProvider
    {
        public JsonArray? RequestMessages { get; private set; }

        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequestMessages = (JsonArray)request.Messages.DeepClone();
            yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "Recovered context accepted.");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "stop");
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

    private sealed class BlockingMcpToolClient : IMcpToolClient
    {
        private readonly StubMcpToolClient _tools = new();

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonArray OpenAiTools => _tools.OpenAiTools;

        public async Task<McpToolResult> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(ct);
            return new McpToolResult(false, "README contents");
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
