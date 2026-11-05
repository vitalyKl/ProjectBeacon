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
    [Fact]
    public async Task RunPipelineSession_ExecutesNativeTurn_WithScopedHarnessAuthorization()
    {
        var temp = Path.Combine(Path.GetTempPath(), "beacon-pipeline-dispatch-" + Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(temp, "project");
        Directory.CreateDirectory(projectRoot);
        await InitializeGitRepositoryAsync(projectRoot, TestContext.Current.CancellationToken);

        try
        {
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
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
            await using var openCode = new ClientOpenCodeServe { SkipRealProcess = true };
            var settings = new WorkstationSettings
            {
                AgentRuntime = "native",
                OpencodeDataDir = Path.Combine(temp, "native-data"),
                LlamaSwapPort = 8080,
                CommandPolicy = "off"
            };
            Func<WorkstationSettings> settingsLoader = () => settings;
            var deps = new DaemonDependencies(
                http,
                llama,
                openCode,
                new OpenCodeAgentRuntime(openCode),
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
                role = "Planner",
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
            Assert.NotEqual(Path.GetFullPath(projectRoot), Path.GetFullPath(capturedAuthorization.WorkingDirectory));
            Assert.Equal(Path.GetFullPath(projectRoot), Path.GetFullPath(capturedAuthorization.ProjectRoot!));
            Assert.True(capturedAuthorization.IsolatedWorkspace);
            Assert.Equal("test-short-lived-token", capturedAuthorization.ApiToken);
            Assert.Equal("Planner", capturedAuthorization.PipelineRole);
            Assert.Empty(capturedAuthorization.AllowedMcpTools!);
            Assert.True(capturedAuthorization.ExpiresAt > DateTime.UtcNow);
            Assert.Equal(generation, tokenHandler.RequestedGeneration);
        }
        finally
        {
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
        await InitializeGitRepositoryAsync(projectRoot, TestContext.Current.CancellationToken);

        try
        {
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
            await using var openCode = new ClientOpenCodeServe { SkipRealProcess = true };
            var settings = new WorkstationSettings
            {
                AgentRuntime = "native",
                OpencodeDataDir = Path.Combine(temp, "native-data"),
                LlamaSwapPort = 8080
            };
            Func<WorkstationSettings> settingsLoader = () => settings;
            var deps = new DaemonDependencies(
                http,
                llama,
                openCode,
                new OpenCodeAgentRuntime(openCode),
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

    private static async Task InitializeGitRepositoryAsync(string projectRoot, CancellationToken ct)
    {
        await GitAsync(projectRoot, ct, "init", "-b", "main");
        await GitAsync(projectRoot, ct, "config", "user.name", "ProjectBeacon Tests");
        await GitAsync(projectRoot, ct, "config", "user.email", "tests@projectbeacon.local");
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
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
    }

    private sealed class HarnessTokenHandler(Guid projectId, Guid commandId, int generation) : HttpMessageHandler
    {
        public int? RequestedGeneration { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post
                || request.RequestUri?.AbsolutePath != $"/v1/commands/{commandId:D}/harness-token")
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
