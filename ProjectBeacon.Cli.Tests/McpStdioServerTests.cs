namespace ProjectBeacon.Cli.Tests;

using System.Text;
using System.Text.Json.Nodes;
using Application.Common;
using ProjectBeacon.Cli.Mcp;

public sealed class McpStdioServerTests
{
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
    public async Task DbTools_WithoutBeaconProjectEnv_ReturnError()
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
                Assert.Contains("BEACON_PROJECT_ID", json, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PipelineTools_WithoutBeaconTaskEnv_ReturnError()
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
                Assert.Contains("BEACON_TASK_ID", json, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
