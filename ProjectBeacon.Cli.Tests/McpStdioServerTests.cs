namespace ProjectBeacon.Cli.Tests;

using System.Text;
using System.Text.Json.Nodes;

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
            Assert.Contains("escapes", frames[1].ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(root, "ok.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void WriteFrame(Stream stream, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        stream.Write(header);
        stream.Write(body);
    }

    private static async Task<List<JsonNode>> ReadAllFramesAsync(Stream stream)
    {
        var frames = new List<JsonNode>();
        var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        while (true)
        {
            var length = -1;
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                    return frames;
                if (line.Length == 0)
                    break;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line["Content-Length:".Length..].Trim());
            }

            if (length < 0)
                return frames;
            var buf = new char[length];
            var n = await reader.ReadBlockAsync(buf, 0, length);
            if (n == 0)
                return frames;
            var node = JsonNode.Parse(new string(buf, 0, n));
            if (node is not null)
                frames.Add(node);
        }
    }
}
