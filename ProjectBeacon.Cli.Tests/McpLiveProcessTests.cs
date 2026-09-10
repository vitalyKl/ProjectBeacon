namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

public sealed class McpLiveProcessTests
{
    [Fact]
    public async Task LiveProcess_ToolsListIncludesWrite_AndParentEscapeRejected()
    {
        var dll = typeof(McpStdioServer).Assembly.Location;
        Assert.True(File.Exists(dll), dll);

        var root = Path.Combine(Path.GetTempPath(), "beacon-mcp-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{dll}\" mcp --root \"{root}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        Assert.NotNull(process);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await WriteFrameAsync(process.StandardInput.BaseStream, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
            await WriteFrameAsync(process.StandardInput.BaseStream, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            await WriteFrameAsync(process.StandardInput.BaseStream, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"write_file","arguments":{"path":"ok.txt","content":"yes"}}}""");
            await WriteFrameAsync(process.StandardInput.BaseStream, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"write_file","arguments":{"path":"../nope.txt","content":"bad"}}}""");
            process.StandardInput.Close();

            var frames = await ReadFramesAsync(process.StandardOutput.BaseStream, 4, cts.Token);
            Assert.Equal(4, frames.Count);
            var toolsJson = frames[1].ToJsonString();
            Assert.Contains("write_file", toolsJson, StringComparison.Ordinal);
            Assert.Contains("apply_patch", toolsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("shell", toolsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"isError\":true", frames[2].ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("escapes", frames[3].ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(root, "ok.txt")));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static async Task WriteFrameAsync(Stream stream, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task<List<JsonNode>> ReadFramesAsync(Stream stream, int count, CancellationToken ct)
    {
        var frames = new List<JsonNode>();
        var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        while (frames.Count < count)
        {
            ct.ThrowIfCancellationRequested();
            var length = -1;
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
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

        return frames;
    }
}
