namespace ProjectBeacon.Cli.Tests;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Mcp;

public sealed class McpApiToolsTests
{
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
        var savedProject = Environment.GetEnvironmentVariable("BEACON_PROJECT_ID");
        var savedTask = Environment.GetEnvironmentVariable("BEACON_TASK_ID");
        Environment.SetEnvironmentVariable("BEACON_PROJECT_ID", projectId.ToString("D"));
        Environment.SetEnvironmentVariable("BEACON_TASK_ID", taskId.ToString("D"));
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://beacon.test/") };
        var api = new BeaconApiClient(http);
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
            await McpStdioServer.RunAsync(root, input, output, api);
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
            Assert.Equal("GET", handler.Calls.Single(call => call.Path == $"/v1/tasks/{taskId:D}/pipeline").Method);
            Assert.All(handler.Calls, call => Assert.Equal(projectId.ToString("D"), call.ProjectHeader));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BEACON_PROJECT_ID", savedProject);
            Environment.SetEnvironmentVariable("BEACON_TASK_ID", savedTask);
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body, string? ProjectHeader)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var project = request.Headers.TryGetValues("X-Project-Id", out var values) ? values.Single() : null;
            var path = request.RequestUri!.PathAndQuery;
            Calls.Add((request.Method.Method, path, body, project));
            var payload = path == "/v1/models"
                ? """{"projectId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","backends":[],"bindings":[]}"""
                : path == "/v1/models/proxy/status"
                    ? """{"available":false}"""
                    : """{"ok":true}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }
}
