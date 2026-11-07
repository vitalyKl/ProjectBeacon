namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Mcp;

public sealed class McpApiToolsTests
{
    [Fact]
    public async Task ApiProblemDetails_ExposeStableErrorCodeAndDetail()
    {
        var env = McpEnvironment.Of(("BEACON_TASK_ID", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        using var http = new HttpClient(new ProblemResponseHandler(
            HttpStatusCode.Forbidden,
            """{"type":"about:blank","title":"Forbidden","status":403,"detail":"Project administration is required.","code":"forbidden"}"""))
        {
            BaseAddress = new Uri("http://beacon.test/")
        };
        var api = new BeaconApiClient(http, env);

        var result = await McpApiTools.PipelineStatusAsync(Guid.NewGuid(), api);

        Assert.True(result.IsError);
        Assert.Contains("HTTP 403 [forbidden]", result.Text, StringComparison.Ordinal);
        Assert.Contains("Project administration is required.", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportSubtaskResult_OversizedSummary_IsRejectedBeforeHttpRequest()
    {
        var env = McpEnvironment.Of(("BEACON_PROJECT_ID", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://beacon.test/") };
        var api = new BeaconApiClient(http, env);

        var result = await McpApiTools.ReportResultAsync(
            Guid.NewGuid(), Guid.NewGuid(), "diff", new string('x', 5001), api);

        Assert.True(result.IsError);
        Assert.Contains("5000-character limit", result.Text);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ToolsList_IncludesControlPlaneTools_AndNoShell()
    {
        var root = TempRoot();
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output);
            output.Position = 0;
            var toolsJson = (await ReadAllFramesAsync(output))[0].ToJsonString();
            foreach (var tool in new[]
            {
                "context_compile", "claim_task", "finish_work", "list_tasks", "list_decisions",
                "list_milestones", "generate_report", "pipeline_start", "deprecate_decision", "model_upsert"
            })
                Assert.Contains(tool, toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("shell", toolsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exec", toolsJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ControlPlane_WithoutApiEnv_ReturnsError_AndFileToolsStillWork()
    {
        var root = TempRoot();
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"context_compile","arguments":{}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"write_file","arguments":{"path":"ok.txt","content":"yes"}}}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Contains("BEACON_API_URL", frames[0].ToJsonString(), StringComparison.Ordinal);
            Assert.Contains("\"isError\":true", frames[0].ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"isError\":true", frames[1].ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(root, "ok.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ControlPlane_CallsWebApiRoutes()
    {
        var projectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var taskId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var env = McpEnvironment.Of(
            ("BEACON_PROJECT_ID", projectId.ToString("D")),
            ("BEACON_TASK_ID", taskId.ToString("D")));
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://beacon.test/") };
        var api = new BeaconApiClient(http, env);
        var root = TempRoot();
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_tasks","arguments":{"status":"Todo"}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"context_compile","arguments":{}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"claim_task","arguments":{}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"finish_work","arguments":{"result":"done","actorId":"cccccccc-cccc-cccc-cccc-cccccccccccc","review":{"reviewerRun":true,"regressionsFound":1,"regressionsFixed":1}}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"model_status","arguments":{}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"task_pipeline_status","arguments":{}}}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);
            Assert.Equal(6, frames.Count);
            foreach (var frame in frames)
                Assert.DoesNotContain("\"isError\":true", frame.ToJsonString(), StringComparison.OrdinalIgnoreCase);

            Assert.Contains(handler.Calls, call => call.Method == "GET" && call.Path == $"/v1/projects/{projectId:D}/tasks?status=Todo");
            var compile = handler.Calls.Single(call => call.Path == $"/v1/projects/{projectId:D}/context/compile");
            Assert.Equal("POST", compile.Method);
            Assert.Contains(taskId.ToString("D"), compile.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"includeHandoff\":false", compile.Body, StringComparison.Ordinal);
            Assert.Equal("PATCH", handler.Calls.Single(call => call.Path.EndsWith("/claim", StringComparison.Ordinal)).Method);
            var finish = handler.Calls.Single(call => call.Path == "/v1/work/finish_work");
            Assert.Contains("\"reviewerRun\":true", finish.Body, StringComparison.Ordinal);
            Assert.Contains("\"result\":\"done\"", finish.Body, StringComparison.Ordinal);
            Assert.Contains(handler.Calls, call => call.Method == "GET" && call.Path == "/v1/models");
            Assert.Contains(handler.Calls, call => call.Method == "GET" && call.Path == "/v1/models/proxy/status");
            Assert.Contains("proxy", frames[4].ToJsonString(), StringComparison.Ordinal);
            Assert.Contains("available", frames[4].ToJsonString(), StringComparison.Ordinal);
            Assert.Contains(handler.Calls, call => call.Method == "GET" && call.Path == $"/v1/tasks/{taskId:D}/pipeline");
            Assert.All(handler.Calls, call => Assert.Equal(projectId.ToString("D"), call.ProjectHeader));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CreateTask_BindsNewTaskToCurrentSession_ForPipelineStatus()
    {
        var projectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var taskId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var env = McpEnvironment.Of(("BEACON_PROJECT_ID", projectId.ToString("D")));
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://beacon.test/") };
        var api = new BeaconApiClient(http, env);
        var root = TempRoot();
        try
        {
            using var input = new MemoryStream();
            WriteFrame(input, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"create_task","arguments":{"title":"Created from chat"}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"task_pipeline_status","arguments":{}}}""");
            WriteFrame(input, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"pipeline_start","arguments":{}}}""");
            input.Position = 0;
            using var output = new MemoryStream();
            await McpStdioServer.RunAsync(root, input, output, api, env);
            output.Position = 0;
            var frames = await ReadAllFramesAsync(output);

            Assert.Equal(3, frames.Count);
            Assert.All(frames, frame => Assert.DoesNotContain(
                "\"isError\":true", frame.ToJsonString(), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(taskId.ToString("D"), env.Get("BEACON_TASK_ID"));
            Assert.Contains(handler.Calls, call =>
                call.Method == "POST" && call.Path == $"/v1/projects/{projectId:D}/tasks");
            Assert.Contains(handler.Calls, call =>
                call.Method == "GET" && call.Path == $"/v1/tasks/{taskId:D}/pipeline");
            Assert.Contains(handler.Calls, call =>
                call.Method == "POST" && call.Path == $"/v1/tasks/{taskId:D}/pipeline/start");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteFrame(Stream stream, string json)
        => stream.Write(Encoding.UTF8.GetBytes(json + "\n"));

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

    [Fact]
    public void Resolve_PartialApiConfig_DoesNotOpenDatabaseMode()
    {
        var env = McpEnvironment.Of(
            ("BEACON_API_URL", "http://127.0.0.1:5083"),
            ("BEACON_API_TOKEN", null));
        var resolved = BeaconApiClient.Resolve(env);
        Assert.True(resolved.Misconfigured);
        Assert.Null(resolved.Client);
    }

    private sealed class ProblemResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json")
            });
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body, string? ProjectHeader)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var project = request.Headers.TryGetValues("X-Project-Id", out var values) ? values.Single() : null;
            var path = request.RequestUri!.PathAndQuery;
            Calls.Add((request.Method.Method, path, body, project));

            var payload = path switch
            {
                "/v1/projects/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/tasks" => """{"id":"dddddddd-dddd-dddd-dddd-dddddddddddd","title":"Created from chat"}""",
                "/v1/tasks/dddddddd-dddd-dddd-dddd-dddddddddddd/pipeline" => """{"taskId":"dddddddd-dddd-dddd-dddd-dddddddddddd","subtasks":[],"sessions":[],"verdicts":[]}""",
                "/v1/models" => """{"projectId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","backends":[],"bindings":[]}""",
                "/v1/models/proxy/status" => """{"available":false}""",
                "/v1/projects/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/context/compile" => """{"success":true,"value":{"briefMarkdown":"# Project\n\n## Goals\n\nDone."}}""",
                _ => """{"ok":true}"""
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }
}
