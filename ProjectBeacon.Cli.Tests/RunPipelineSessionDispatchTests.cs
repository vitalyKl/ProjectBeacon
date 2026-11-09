namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Runtime;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class RunPipelineSessionDispatchTests
{
    [Theory]
    [InlineData("Planner")]
    [InlineData("Actor")]
    [InlineData("Review")]
    public async Task RunPipelineSession_ExecutesNativeTurn_WithScopedHarnessAuthorization(string role)
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-pipeline-dispatch-" + Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(temp, "project");
        var sessionId = Guid.NewGuid();
        var subtaskId = string.Equals(role, "Actor", StringComparison.Ordinal)
            ? Guid.NewGuid()
            : (Guid?)null;
        Directory.CreateDirectory(projectRoot);

        try
        {
            await InitializeGitRepositoryAsync(projectRoot, TestContext.Current.CancellationToken);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var commandId = Guid.NewGuid();
            const int generation = 3;
            AgentHarnessAuthorization? capturedAuthorization = null;
            string? requestedModel = null;
            var tokenHandler = new HarnessTokenHandler(projectId, commandId, generation);
            using var http = new HttpClient(tokenHandler) { BaseAddress = new Uri("http://localhost/") };
            await using var llama = new ClientLlamaSwap
            {
                SkipRealProcess = true,
                UseOwnSwapper = false,
                ConfigFile = Path.Combine(temp, "llamaswap.yaml")
            };
            var settings = new WorkstationSettings
            {
                AgentDataDir = Path.Combine(temp, "native-data"),
                LlamaSwapPort = 8080,
                CommandPolicy = "off"
            };
            Func<WorkstationSettings> settingsLoader = () => settings;
            var deps = new DaemonDependencies(
                http,
                llama,
                settingsLoader,
                runtimeFactory: llamaSync => new NativeAgentRuntime(
                    http,
                    llama,
                    llamaSync,
                    settingsLoader,
                    providerFactory: (_, _, _) => new StubProvider(model => requestedModel = model),
                    mcpClientFactory: (authorization, _) =>
                    {
                        capturedAuthorization = authorization;
                        return Task.FromResult<IMcpToolClient>(new StubMcpToolClient());
                    }));
            var state = new DaemonRuntimeState("http://localhost", null);
            var timing = new DaemonTiming
            {
                ChatPollInterval = TimeSpan.FromMilliseconds(1),
                ChatMaxDuration = TimeSpan.FromSeconds(5)
            };
            await using var services = DaemonServices.Build(deps, timing, state);
            var executor = services.GetRequiredService<IDaemonCommandExecutor>();

            var payload = JsonSerializer.Serialize(new
            {
                projectId,
                taskId,
                sessionId,
                role,
                subtaskId,
                reportsDiff = false,
                title = "Native pipeline dispatch test",
                prompt = "Respond with the completion marker.",
                model = "beacon-local/test-model",
                contextLimit = 4096,
                reasoningEffort = "medium"
            });

            var (ok, resultJson, error) = await executor.ExecuteAsync(new WorkstationDaemon.CommandWire
            {
                Id = commandId,
                Kind = WorkstationCommandKind.RunPipelineSession,
                Generation = generation,
                LocalRoot = projectRoot,
                PayloadJson = payload
            }, TestContext.Current.CancellationToken);

            Assert.True(ok, error);
            using var result = JsonDocument.Parse(resultJson!);
            Assert.Equal(sessionId, result.RootElement.GetProperty("pipelineSessionId").GetGuid());
            var externalSessionId = result.RootElement.GetProperty("externalSessionId").GetString();
            Assert.True(Guid.TryParseExact(externalSessionId, "N", out _));
            Assert.Equal("stop", result.RootElement.GetProperty("finish").GetString());

            Assert.Equal("test-model", requestedModel);
            Assert.NotNull(capturedAuthorization);
            Assert.Equal(projectId, capturedAuthorization!.ProjectId);
            Assert.Equal(taskId, capturedAuthorization.TaskId);
            Assert.Equal(commandId, capturedAuthorization.CommandId);
            Assert.Equal(generation, capturedAuthorization.Generation);
            var worktreePath = Path.GetFullPath(capturedAuthorization.WorkingDirectory);
            Assert.NotEqual(Path.GetFullPath(projectRoot), worktreePath);
            Assert.Equal(Path.GetFullPath(projectRoot), Path.GetFullPath(capturedAuthorization.ProjectRoot!));
            var worktreeParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ProjectBeacon", "agent-worktrees"))
                + Path.DirectorySeparatorChar;
            Assert.StartsWith(worktreeParent, worktreePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            // The built-in Beacon MCP child must use exactly the same isolated root as the agent.
            var mcpStart = McpStdioClient.CreateStartInfo(worktreePath, capturedAuthorization);
            Assert.Equal(worktreePath, Path.GetFullPath(mcpStart.WorkingDirectory));
            var mcpArguments = mcpStart.ArgumentList.ToArray();
            Assert.True(mcpArguments.Length >= 3);
            Assert.Equal("mcp", mcpArguments[^3]);
            Assert.Equal("--root", mcpArguments[^2]);
            Assert.Equal(worktreePath, Path.GetFullPath(mcpArguments[^1]));
            Assert.Equal(role, mcpStart.Environment["BEACON_PIPELINE_ROLE"]);

            Assert.True(capturedAuthorization.IsolatedWorkspace);
            Assert.Equal("test-short-lived-token", capturedAuthorization.ApiToken);
            Assert.Equal(role, capturedAuthorization.PipelineRole);
            Assert.Equal(subtaskId, capturedAuthorization.SubtaskId);
            Assert.Empty(capturedAuthorization.AllowedMcpTools!);
            Assert.True(capturedAuthorization.ExpiresAt > DateTime.UtcNow);
            Assert.Equal(generation, tokenHandler.RequestedGeneration);
            Assert.True(tokenHandler.ProgressRequests > 0, "The pipeline must report progress through the command API.");
        }
        finally
        {
            await RemovePreservedWorktreesAsync(projectRoot, sessionId);
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public async Task RunPipelineSession_ReturnsFailureWhenNativeTurnWasAborted()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-pipeline-aborted-" + Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(temp, "project");
        var sessionId = Guid.NewGuid();
        Directory.CreateDirectory(projectRoot);

        try
        {
            await InitializeGitRepositoryAsync(projectRoot, TestContext.Current.CancellationToken);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var commandId = Guid.NewGuid();
            const int generation = 1;
            var tokenHandler = new HarnessTokenHandler(projectId, commandId, generation);
            using var http = new HttpClient(tokenHandler) { BaseAddress = new Uri("http://localhost/") };
            await using var llama = new ClientLlamaSwap
            {
                SkipRealProcess = true,
                UseOwnSwapper = false,
                ConfigFile = Path.Combine(temp, "llamaswap.yaml")
            };
            var settings = new WorkstationSettings
            {
                AgentDataDir = Path.Combine(temp, "native-data"),
                LlamaSwapPort = 8080
            };
            Func<WorkstationSettings> settingsLoader = () => settings;
            var deps = new DaemonDependencies(
                http,
                llama,
                settingsLoader,
                runtimeFactory: llamaSync => new NativeAgentRuntime(
                    http,
                    llama,
                    llamaSync,
                    settingsLoader,
                    providerFactory: (_, _, _) => new StubProvider(_ => { }, "aborted"),
                    mcpClientFactory: (_, _) => Task.FromResult<IMcpToolClient>(new StubMcpToolClient())));
            var state = new DaemonRuntimeState("http://localhost", null);
            var timing = new DaemonTiming
            {
                ChatPollInterval = TimeSpan.FromMilliseconds(1),
                ChatMaxDuration = TimeSpan.FromSeconds(5)
            };
            await using var services = DaemonServices.Build(deps, timing, state);
            var executor = services.GetRequiredService<IDaemonCommandExecutor>();
            var payload = JsonSerializer.Serialize(new
            {
                projectId,
                taskId,
                sessionId,
                role = "Planner",
                title = "Aborted pipeline dispatch test",
                prompt = "Start and abort.",
                model = "beacon-local/test-model"
            });

            var (ok, resultJson, error) = await executor.ExecuteAsync(new WorkstationDaemon.CommandWire
            {
                Id = commandId,
                Kind = WorkstationCommandKind.RunPipelineSession,
                Generation = generation,
                LocalRoot = projectRoot,
                PayloadJson = payload
            }, TestContext.Current.CancellationToken);

            Assert.False(ok);
            Assert.Null(resultJson);
            Assert.Contains("aborted", error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await RemovePreservedWorktreesAsync(projectRoot, sessionId);
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public async Task RunPipelineSession_AppliesWorktreeWhenActorReportedDoneBeforeProgressFailure()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-pipeline-done-after-error-" + Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(temp, "project");
        var pipelineSessionId = Guid.NewGuid();
        Directory.CreateDirectory(projectRoot);

        try
        {
            await InitializeGitRepositoryAsync(projectRoot, TestContext.Current.CancellationToken);
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var subtaskId = Guid.NewGuid();
            var commandId = Guid.NewGuid();
            const int generation = 2;
            var tokenHandler = new DoneAfterReportTokenHandler(projectId, taskId, subtaskId, commandId, generation);
            using var http = new HttpClient(tokenHandler) { BaseAddress = new Uri("http://localhost/") };
            await using var llama = new ClientLlamaSwap
            {
                SkipRealProcess = true,
                UseOwnSwapper = false,
                ConfigFile = Path.Combine(temp, "llamaswap.yaml")
            };
            var settings = new WorkstationSettings
            {
                AgentDataDir = Path.Combine(temp, "native-data"),
                LlamaSwapPort = 8080,
                CommandPolicy = "off"
            };
            Func<WorkstationSettings> settingsLoader = () => settings;
            var deps = new DaemonDependencies(
                http,
                llama,
                settingsLoader,
                runtimeFactory: llamaSync => new NativeAgentRuntime(
                    http,
                    llama,
                    llamaSync,
                    settingsLoader,
                    providerFactory: (_, _, _) => new ActorReportProvider(),
                    mcpClientFactory: (authorization, _) => Task.FromResult<IMcpToolClient>(
                        new ReportAndEditMcpToolClient(authorization.WorkingDirectory, tokenHandler.MarkSubtaskReported))));
            var state = new DaemonRuntimeState("http://localhost", null);
            var timing = new DaemonTiming
            {
                ChatPollInterval = TimeSpan.FromMilliseconds(1),
                ChatMaxDuration = TimeSpan.FromSeconds(5)
            };
            await using var services = DaemonServices.Build(deps, timing, state);
            var executor = services.GetRequiredService<IDaemonCommandExecutor>();

            var payload = JsonSerializer.Serialize(new
            {
                projectId,
                taskId,
                sessionId = pipelineSessionId,
                subtaskId,
                role = "Actor",
                title = "Actor Done recovery test",
                prompt = "Make the assigned change and report it.",
                model = "beacon-local/test-model",
                reportsDiff = true
            });

            var (ok, resultJson, error) = await executor.ExecuteAsync(new WorkstationDaemon.CommandWire
            {
                Id = commandId,
                Kind = WorkstationCommandKind.RunPipelineSession,
                Generation = generation,
                LocalRoot = projectRoot,
                PayloadJson = payload
            }, TestContext.Current.CancellationToken);

            Assert.True(ok, error);
            Assert.True(tokenHandler.SubtaskReported);
            Assert.Equal("actor change\n", await File.ReadAllTextAsync(Path.Combine(projectRoot, "actor-output.txt")));
            using var result = JsonDocument.Parse(resultJson!);
            Assert.True(result.RootElement.GetProperty("serverReportedDone").GetBoolean());
            Assert.True(result.RootElement.GetProperty("recoveredAfterTurnError").GetBoolean());
            Assert.Equal($"diff/{subtaskId:N}", result.RootElement.GetProperty("diffRef").GetString());
            Assert.True(tokenHandler.WorkspaceDiffStored);
        }
        finally
        {
            await RemovePreservedWorktreesAsync(projectRoot, pipelineSessionId);
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static async Task InitializeGitRepositoryAsync(string projectRoot, CancellationToken ct)
    {
        await GitAsync(projectRoot, ct, "init", "-b", "main");
        await GitAsync(projectRoot, ct, "config", "core.autocrlf", "false");
        await GitAsync(projectRoot, ct, "config", "user.name", "ProjectBeacon Tests");
        await GitAsync(projectRoot, ct, "config", "user.email", "tests@projectbeacon.local");
        await GitAsync(projectRoot, ct, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "README.md"), "pipeline fixture\n", ct);
        await GitAsync(projectRoot, ct, "add", "README.md");
        await GitAsync(projectRoot, ct, "commit", "-m", "initialize pipeline fixture");
    }

    private static async Task RemovePreservedWorktreesAsync(string projectRoot, Guid sessionId)
    {
        var parent = Path.Combine(Path.GetTempPath(), "ProjectBeacon", "agent-worktrees");
        if (!Directory.Exists(parent))
            return;

        foreach (var worktree in Directory.EnumerateDirectories(parent, sessionId.ToString("N") + "-*"))
        {
            try { await GitAsync(projectRoot, CancellationToken.None, "worktree", "remove", "--force", worktree); }
            catch { }
            try { if (Directory.Exists(worktree)) Directory.Delete(worktree, recursive: true); } catch { }
        }

        try { await GitAsync(projectRoot, CancellationToken.None, "worktree", "prune"); } catch { }
    }

    private static async Task GitAsync(string directory, CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git for the pipeline fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            var timedOutOutput = await stdout;
            var timedOutError = await stderr;
            throw new TimeoutException(
                $"git {string.Join(' ', arguments)} exceeded the 30-second test command timeout: {timedOutError}{timedOutOutput}");
        }

        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
    }

    private sealed class DoneAfterReportTokenHandler(
        Guid projectId, Guid taskId, Guid subtaskId, Guid commandId, int generation) : HttpMessageHandler
    {
        private int _reported;
        private int _failNextProgress;
        public bool SubtaskReported => Volatile.Read(ref _reported) == 1;
        public bool WorkspaceDiffStored { get; private set; }

        public void MarkSubtaskReported()
        {
            Interlocked.Exchange(ref _reported, 1);
            Interlocked.Exchange(ref _failNextProgress, 1);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath.TrimEnd('/');
            if (request.Method == HttpMethod.Post && path == $"/v1/commands/{commandId:D}/harness-token")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (body.RootElement.GetProperty("generation").GetInt32() != generation)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        token = "test-short-lived-token",
                        projectId,
                        commandId,
                        generation,
                        expiresAt = DateTime.UtcNow.AddMinutes(2),
                        allowedMcpTools = new[] { "subtask_report_result" }
                    }), System.Text.Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Get && path == $"/v1/tasks/{taskId:D}/pipeline")
            {
                var state = SubtaskReported ? "Done" : "InProgress";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        subtasks = new[] { new { id = subtaskId, status = state } }
                    }), System.Text.Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Post && path is not null && path.EndsWith("/progress", StringComparison.OrdinalIgnoreCase))
            {
                if (SubtaskReported && Interlocked.Exchange(ref _failNextProgress, 0) == 1)
                    throw new HttpRequestException("simulated progress transport failure after Actor Done");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && path == $"/v1/tasks/{taskId:D}/workspace")
            {
                WorkspaceDiffStored = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class ActorReportProvider : IChatProvider
    {
        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ChatProviderEvent(
                ChatProviderEventKind.ToolCallDelta,
                ToolIndex: 0,
                ToolCallId: "actor-report",
                ToolName: "subtask_report_result",
                ToolArguments: """{"summary":"The change is complete.","diffRef":""}""");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "tool_calls");
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: "done");
            await Task.CompletedTask;
        }
    }

    private sealed class ReportAndEditMcpToolClient(string workingDirectory, Action onReported) : IMcpToolClient
    {
        public JsonArray OpenAiTools { get; } =
        [
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "subtask_report_result",
                    ["description"] = "Report the Actor result.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["summary"] = new JsonObject { ["type"] = "string" },
                            ["diffRef"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray { JsonValue.Create("summary") }
                    }
                }
            }
        ];

        public async Task<McpToolResult> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
        {
            if (!string.Equals(name, "subtask_report_result", StringComparison.Ordinal))
                return new McpToolResult(true, "unexpected tool");
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "actor-output.txt"), "actor change\n", ct);
            onReported();
            return new McpToolResult(false, "Subtask status: Done.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HarnessTokenHandler(Guid projectId, Guid commandId, int generation) : HttpMessageHandler
    {
        public int? RequestedGeneration { get; private set; }

        private int _progressRequests;
        public int ProgressRequests => Volatile.Read(ref _progressRequests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var path = request.RequestUri?.AbsolutePath.TrimEnd('/');
            if (request.Method == HttpMethod.Post
                && path is not null
                && path.EndsWith("/progress", StringComparison.OrdinalIgnoreCase))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2
                    || !Guid.TryParse(segments[^2], out var progressCommandId)
                    || progressCommandId != commandId)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                using var progress = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (progress.RootElement.GetProperty("generation").GetInt32() != generation)
                    return new HttpResponseMessage(HttpStatusCode.Conflict);

                Interlocked.Increment(ref _progressRequests);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path != $"/v1/commands/{commandId:D}/harness-token")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestedGeneration = body.RootElement.GetProperty("generation").GetInt32();
            if (RequestedGeneration != generation)
                return new HttpResponseMessage(HttpStatusCode.BadRequest);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    token = "test-short-lived-token",
                    projectId,
                    commandId,
                    generation,
                    expiresAt = DateTime.UtcNow.AddMinutes(2),
                    allowedMcpTools = Array.Empty<string>()
                }), System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubProvider(Action<string> setModel, string finishReason = "stop") : IChatProvider
    {
        public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(
            ChatProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            setModel(request.Model);
            yield return new ChatProviderEvent(ChatProviderEventKind.TextDelta, Text: "Pipeline agent completed.");
            yield return new ChatProviderEvent(ChatProviderEventKind.Usage, PromptTokens: 12, CompletionTokens: 7);
            yield return new ChatProviderEvent(ChatProviderEventKind.Finished, FinishReason: finishReason);
            await Task.CompletedTask;
        }
    }

    private sealed class StubMcpToolClient : IMcpToolClient
    {
        public JsonArray OpenAiTools { get; } = [];

        public Task<McpToolResult> CallToolAsync(string name, JsonObject arguments, CancellationToken ct) =>
            Task.FromResult(new McpToolResult(false, "unused"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
