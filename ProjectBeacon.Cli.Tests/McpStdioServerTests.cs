namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Common;
using ProjectBeacon.Cli.Mcp;

public sealed class McpStdioServerTests
{

    [Fact]
    public async Task ToolsList_FiltersToolsUsingActiveSubtaskAllowlist()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var subtaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var env = McpEnvironment.Of(
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_SUBTASK_ID", subtaskId.ToString("D")),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"subtasks":[{"id":"SUBTASK_ID","status":"InProgress","allowedMcpTools":["read_file"],"allowedPaths":["src"]}]}""".Replace("SUBTASK_ID", subtaskId.ToString("D"), StringComparison.Ordinal),
                Encoding.UTF8,
                "application/json")
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            var toolsJson = frames[0]["result"]!["tools"]!.ToJsonString();
            Assert.Contains("read_file", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("write_file", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("task_pipeline_status", toolsJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ToolsList_PlannerKeepsSupervisorToolsButNotActorFileWrites()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var subtaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var env = McpEnvironment.Of(
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_PIPELINE_ROLE", "Planner"),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"subtasks":[{"id":"SUBTASK_ID","status":"InProgress","allowedMcpTools":["read_file"],"allowedPaths":["src"]}]}"""
                    .Replace("SUBTASK_ID", subtaskId.ToString("D"), StringComparison.Ordinal),
                Encoding.UTF8,
                "application/json")
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            var toolsJson = frames[0]["result"]!["tools"]!.ToJsonString();
            Assert.Contains("read_file", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("write_file", toolsJson, StringComparison.Ordinal);
            Assert.Contains("task_create_subtask", toolsJson, StringComparison.Ordinal);
            Assert.Contains("pipeline_start_actor", toolsJson, StringComparison.Ordinal);
            Assert.Contains("pipeline_launch_session", toolsJson, StringComparison.Ordinal);
            Assert.Contains("task_pipeline_status", toolsJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PlannerCanLaunchActorWhileItsSubtaskIsRestricted()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var sessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var subtaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var launchPath = $"/v1/sessions/{sessionId:D}/launch";
        var launchCalled = false;
        var env = McpEnvironment.Of(
            ("BEACON_API_URL", "https://beacon.test/"),
            ("BEACON_API_TOKEN", "test-token"),
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_PIPELINE_ROLE", "Planner"),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == launchPath)
            {
                launchCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == $"/v1/tasks/{taskId:D}/pipeline")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"sessions":[{"id":"SESSION_ID"}],"subtasks":[{"id":"SUBTASK_ID","status":"InProgress","allowedMcpTools":["read_file"],"allowedPaths":["src"]}]}"""
                            .Replace("SESSION_ID", sessionId.ToString("D"), StringComparison.Ordinal)
                            .Replace("SUBTASK_ID", subtaskId.ToString("D"), StringComparison.Ordinal),
                        Encoding.UTF8,
                        "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "pipeline_launch_session",
                    arguments = new { sessionId = sessionId.ToString("D") }
                }
            }));
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            Assert.True(launchCalled);
            Assert.DoesNotContain("\"isError\":true", frames[0].ToJsonString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ToolsList_ReviewerGetsReadOnlyWorkspaceAndReviewControlsOnly()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var subtaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var env = McpEnvironment.Of(
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_PIPELINE_ROLE", "Review"),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"subtasks":[{"id":"SUBTASK_ID","status":"Done","allowedMcpTools":[],"allowedPaths":[]}]}"""
                    .Replace("SUBTASK_ID", subtaskId.ToString("D"), StringComparison.Ordinal),
                Encoding.UTF8,
                "application/json")
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            var toolsJson = frames[0]["result"]!["tools"]!.ToJsonString();
            Assert.Contains("read_file", toolsJson, StringComparison.Ordinal);
            Assert.Contains("task_pipeline_status", toolsJson, StringComparison.Ordinal);
            Assert.Contains("task_review_verdict", toolsJson, StringComparison.Ordinal);
            Assert.Contains("pipeline_approve", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("write_file", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("task_create_subtask", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("pipeline_start_actor", toolsJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PlannerCannotOverridePipelineTaskWithToolArguments()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var otherTaskId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var subtaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var apiCallCount = 0;
        var env = McpEnvironment.Of(
            ("BEACON_API_URL", "https://beacon.test/"),
            ("BEACON_API_TOKEN", "test-token"),
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_PIPELINE_ROLE", "Planner"),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(_ =>
        {
            apiCallCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "pipeline_start_actor",
                    arguments = new
                    {
                        taskId = otherTaskId.ToString("D"),
                        subtaskId = subtaskId.ToString("D")
                    }
                }
            }));
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            Assert.Equal(0, apiCallCount);
            Assert.Contains("must match BEACON_TASK_ID", frames[0].ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PlannerCannotLaunchSessionOutsideCurrentTask()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var unrelatedSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var launchCalled = false;
        var env = McpEnvironment.Of(
            ("BEACON_API_URL", "https://beacon.test/"),
            ("BEACON_API_TOKEN", "test-token"),
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_PIPELINE_ROLE", "Planner"),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == $"/v1/tasks/{taskId:D}/pipeline")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"sessions":[{"id":"33333333-3333-3333-3333-333333333333"}],"subtasks":[]}""", Encoding.UTF8, "application/json")
                };
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == $"/v1/sessions/{unrelatedSessionId:D}/launch")
            {
                launchCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "pipeline_launch_session",
                    arguments = new { sessionId = unrelatedSessionId.ToString("D") }
                }
            }));
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            Assert.False(launchCalled);
            Assert.Contains("does not belong to the current pipeline task", frames[0].ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ActorCannotLaunchAnotherPipelineSessionEvenWhenSubtaskAllowlistIsEmpty()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var ownSubtaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var otherSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var launchCalled = false;
        var env = McpEnvironment.Of(
            ("BEACON_API_URL", "https://beacon.test/"),
            ("BEACON_API_TOKEN", "test-token"),
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_SUBTASK_ID", ownSubtaskId.ToString("D")),
            ("BEACON_PIPELINE_ROLE", "Actor"),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == $"/v1/sessions/{otherSessionId:D}/launch")
            {
                launchCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "pipeline_launch_session",
                    arguments = new { sessionId = otherSessionId.ToString("D") }
                }
            }));
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            Assert.False(launchCalled);
            Assert.Contains("not allowed for the Actor pipeline role", frames[0].ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ActorCannotReportResultForAnotherSubtask()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var ownSubtaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var otherSubtaskId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var apiCallCount = 0;
        var env = McpEnvironment.Of(
            ("BEACON_API_URL", "https://beacon.test/"),
            ("BEACON_API_TOKEN", "test-token"),
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_SUBTASK_ID", ownSubtaskId.ToString("D")),
            ("BEACON_PIPELINE_ROLE", "Actor"),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(_ =>
        {
            apiCallCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "subtask_report_result",
                    arguments = new
                    {
                        subtaskId = otherSubtaskId.ToString("D"),
                        diffRef = "working-tree",
                        summary = "completed"
                    }
                }
            }));
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            Assert.Equal(0, apiCallCount);
            Assert.Contains("only report the current subtask", frames[0].ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ToolsList_EmptySubtaskListExposesPlannerTools()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var env = McpEnvironment.Of(
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_PIPELINE_ROLE", "Planner"),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"subtasks":[]}""", Encoding.UTF8, "application/json")
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            var toolsJson = frames[0]["result"]!["tools"]!.ToJsonString();
            Assert.Contains("task_create_subtask", toolsJson, StringComparison.Ordinal);
            Assert.Contains("context_compile", toolsJson, StringComparison.Ordinal);
            Assert.Contains("pipeline_start_actor", toolsJson, StringComparison.Ordinal);
            Assert.Contains("pipeline_launch_session", toolsJson, StringComparison.Ordinal);
            Assert.Contains("read_file", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("write_file", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("apply_patch", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("pipeline_approve", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("create_task", toolsJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ToolsList_EmptySubtaskListWithoutPlannerRoleFailsClosed()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var env = McpEnvironment.Of(
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"subtasks":[]}""", Encoding.UTF8, "application/json")
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            Assert.Equal("[]", frames[0]["result"]!["tools"]!.ToJsonString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ToolsList_MalformedPipelineResponseFailsClosed()
    {
        var taskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var env = McpEnvironment.Of(
            ("BEACON_TASK_ID", taskId.ToString("D")),
            ("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        using var http = new HttpClient(new StubApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"unexpected":[]}""", Encoding.UTF8, "application/json")
        }))
        {
            BaseAddress = new Uri("https://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Single(frames);
            Assert.Equal("[]", frames[0]["result"]!["tools"]!.ToJsonString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ToolsList_IncludesWrite_AndNoShell_ThenExitsWhenStdinCloses()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            input.Position = 0;

            using var output = new MemoryStream();
            var code = await McpStdioServer.RunAsync(root, input, output);
            Assert.Equal(0, code);

            output.Position = 0;
            var listed = await ReadAllFramesAsync(output);
            Assert.Equal(2, listed.Count);
            var toolsJson = listed[1].ToJsonString();
            Assert.Contains("write_file", toolsJson, StringComparison.Ordinal);
            Assert.Contains("read_file", toolsJson, StringComparison.Ordinal);
            Assert.Contains("apply_patch", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("shell", toolsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exec", toolsJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task WriteFile_ThenParentEscape_IsError()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"write_file","arguments":{"path":"ok.txt","content":"yes"}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"write_file","arguments":{"path":"../nope.txt","content":"bad"}}}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Equal(2, frames.Count);
            Assert.DoesNotContain("\"isError\":true", frames[0].ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(WorkspacePath.ParentSegmentNotAllowed, ContentText(frames[1]));
            Assert.True(File.Exists(Path.Combine(root, "ok.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IndexTools_AreListed_AndTreeSearchWork()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "app.cs"), "hello beacon\n");
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_tree","arguments":{}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_code","arguments":{"query":"hello beacon"}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"search_code","arguments":{"query":"no-such-token-xyz"}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_changed_scope","arguments":{}}}""");
            input.Position = 0;

            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Equal(5, frames.Count);

            var toolsJson = frames[0].ToJsonString();
            Assert.Contains("get_tree", toolsJson, StringComparison.Ordinal);
            Assert.Contains("search_code", toolsJson, StringComparison.Ordinal);
            Assert.Contains("get_changed_scope", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("shell", toolsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exec", toolsJson, StringComparison.OrdinalIgnoreCase);

            var tree = frames[1].ToJsonString();
            Assert.Contains("# Tree (2 entries)", tree, StringComparison.Ordinal);
            Assert.Contains("src/", tree, StringComparison.Ordinal);
            Assert.Contains("app.cs", tree, StringComparison.Ordinal);
            Assert.DoesNotContain("\"isError\":true", tree, StringComparison.OrdinalIgnoreCase);

            var found = frames[2].ToJsonString();
            Assert.Contains("src/app.cs:1: hello beacon", found, StringComparison.Ordinal);
            Assert.DoesNotContain("\"isError\":true", found, StringComparison.OrdinalIgnoreCase);

            var none = frames[3].ToJsonString();
            Assert.Contains("No matches", none, StringComparison.Ordinal);
            Assert.DoesNotContain("\"isError\":true", none, StringComparison.OrdinalIgnoreCase);

            var changed = frames[4].ToJsonString();
            Assert.Contains("\"isError\":true", changed, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task HashRange_IsListed_AndReturnsBareHex()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "app.cs"), "alpha\nbeta\ngamma\n");
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"hash_range","arguments":{"path":"src/app.cs","startLine":1,"endLine":3}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"hash_range","arguments":{"path":"src/app.cs","startLine":2,"endLine":2}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"hash_range","arguments":{"path":"src/app.cs","startLine":1,"endLine":99}}}""");
            input.Position = 0;

            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Equal(4, frames.Count);

            var toolsJson = frames[0].ToJsonString();
            Assert.Contains("hash_range", toolsJson, StringComparison.Ordinal);

            var full = frames[1].ToJsonString();
            Assert.DoesNotContain("\"isError\":true", full, StringComparison.OrdinalIgnoreCase);
            var hashText = frames[1]!["result"]!["content"]![0]!["text"]!.GetValue<string>()!;
            Assert.Matches("^[0-9a-f]{64}$", hashText);

            var single = frames[2].ToJsonString();
            Assert.DoesNotContain("\"isError\":true", single, StringComparison.OrdinalIgnoreCase);
            var singleHash = frames[2]!["result"]!["content"]![0]!["text"]!.GetValue<string>()!;
            Assert.Matches("^[0-9a-f]{64}$", singleHash);
            Assert.NotEqual(hashText, singleHash);

            var overflow = frames[3].ToJsonString();
            Assert.Contains("\"isError\":true", overflow, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ToolsList_IncludesFileAndPipelineTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            input.Position = 0;

            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            var toolsJson = frames[0].ToJsonString();

            var expected = new[]
            {
                "read_file", "write_file", "apply_patch", "get_tree", "search_code", "get_changed_scope",
                "model_bind", "model_status", "task_create_subtask",
                "subtask_report_result", "task_review_verdict", "task_pipeline_status"
            };
            foreach (var tool in expected)
                Assert.Contains(tool, toolsJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ControlPlaneTools_WithoutApiCredentials_ReturnError()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"model_status","arguments":{}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"model_bind","arguments":{"role":"actor","modelBackendId":"11111111-1111-1111-1111-111111111111"}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"task_pipeline_status","arguments":{}}}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Equal(3, frames.Count);
            foreach (var frame in frames)
            {
                var json = frame.ToJsonString();
                Assert.Contains("\"isError\":true", json, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("BEACON_API_URL and BEACON_API_TOKEN are required", json, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PipelineTools_WithoutApiCredentials_ReturnError()
    {
        var env = McpEnvironment.Of(("BEACON_PROJECT_ID", Guid.NewGuid().ToString("D")));
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"task_create_subtask","arguments":{"instructions":"do it"}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"subtask_report_result","arguments":{"subtaskId":"11111111-1111-1111-1111-111111111111","diffRef":"refs/heads/x","summary":"done"}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"task_review_verdict","arguments":{"verdict":"approve","note":"lgtm"}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"task_pipeline_status","arguments":{}}}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, null, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Equal(4, frames.Count);
            foreach (var frame in frames)
            {
                var json = frame.ToJsonString();
                Assert.Contains("\"isError\":true", json, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("BEACON_API_URL and BEACON_API_TOKEN are required", json, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PartialApiCredentialsFailForControlPlaneButFileToolsRemainAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "hello.txt"), "file tools still work");
        try
        {
            var env = McpEnvironment.Of(("BEACON_API_URL", "http://localhost:5083"));
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"model_status","arguments":{}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"read_file","arguments":{"path":"hello.txt"}}}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, null, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Equal(2, frames.Count);
            Assert.Contains("BEACON_API_URL and BEACON_API_TOKEN must both be set", frames[0].ToJsonString(), StringComparison.Ordinal);
            Assert.Contains("file tools still work", frames[1].ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class StubApiHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }

    private static void WriteFrame(Stream stream, string json)
    {
        var body = Encoding.UTF8.GetBytes(json + "\n");
        stream.Write(body);
    }

    private static string? ContentText(JsonNode frame)
        => frame?["result"]?["content"]?[0]?["text"]?.GetValue<string>();

    private static async Task<List<JsonNode>> ReadAllFramesAsync(Stream stream)
    {
        var frames = new List<JsonNode>();
        var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
                return frames;
            if (line.Length == 0)
                continue;
            var node = JsonNode.Parse(line);
            if (node is not null)
                frames.Add(node);
        }
    }
}
