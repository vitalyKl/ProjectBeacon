namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;

/// <summary>
/// Minimal MCP stdio client for the existing <c>beacon mcp</c> server. The child receives a
/// short-lived token through its environment, never through argv. Its inherited environment is
/// reduced to OS/runtime variables plus the explicitly scoped Beacon credentials.
/// </summary>
internal sealed class McpStdioClient : IMcpToolClient
{
    private const int MaxToolResultCharacters = 16_000;
    private static readonly string[] SafeInheritedEnvironment =
    [
        "PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "TEMP", "TMP", "TMPDIR", "SystemRoot", "WINDIR",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_CLI_HOME", "DOTNET_MULTILEVEL_LOOKUP",
        "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH"
    ];

    private readonly Process _process;
    private readonly SemaphoreSlim _protocol = new(1, 1);
    private int _nextId;
    private bool _disposed;

    private McpStdioClient(Process process)
    {
        _process = process;
        _ = DrainStandardErrorAsync(process);
    }

    public JsonArray OpenAiTools { get; private set; } = [];

    public static async Task<McpStdioClient> StartAsync(
        AgentHarnessAuthorization authorization,
        CancellationToken ct)
    {
        if (authorization.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("The native harness authorization has expired.");
        if (string.IsNullOrWhiteSpace(authorization.WorkingDirectory)
            || !Directory.Exists(authorization.WorkingDirectory))
            throw new DirectoryNotFoundException("The selected project directory does not exist.");

        var startInfo = CreateStartInfo(authorization.WorkingDirectory, authorization);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The local Beacon MCP process could not be started.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            process.Dispose();
            throw new InvalidOperationException(
                "Could not launch the local Beacon MCP server. Check that the installed beacon client executable is available.",
                ex);
        }

        var client = new McpStdioClient(process);
        try
        {
            await client.InitializeAsync(ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<McpToolResult> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        var response = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments.DeepClone()
        }, ct);

        var isError = response["isError"]?.GetValue<bool>() == true;
        var content = response["content"] as JsonArray;
        var output = new StringBuilder();
        if (content is not null)
        {
            foreach (var item in content)
            {
                if (item is null)
                    continue;
                var type = item["type"]?.GetValue<string>();
                if (type == "text" && item["text"]?.GetValue<string>() is { } text)
                {
                    if (output.Length > 0)
                        output.Append('\n');
                    output.Append(text);
                }
                else if (type is not null)
                {
                    if (output.Length > 0)
                        output.Append('\n');
                    output.Append(item.ToJsonString());
                }

                if (output.Length > MaxToolResultCharacters)
                {
                    output.Length = MaxToolResultCharacters;
                    output.Append("\n[tool result truncated by ProjectBeacon]");
                    break;
                }
            }
        }

        if (output.Length == 0 && response["structuredContent"] is { } structured)
            output.Append(structured.ToJsonString());

        return new McpToolResult(isError, output.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _process.StandardInput.Close(); } catch { }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            try { await _process.WaitForExitAsync(CancellationToken.None); } catch { }
        }
        _process.Dispose();
        _protocol.Dispose();
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var init = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "projectbeacon-native",
                ["version"] = "1"
            }
        }, ct);
        if (init["protocolVersion"]?.GetValue<string>() is not { Length: > 0 })
            throw new InvalidDataException("The local MCP server did not return a protocol version.");

        await WriteNotificationAsync("notifications/initialized", new JsonObject(), ct);
        var listed = await RequestAsync("tools/list", new JsonObject(), ct);
        if (listed["tools"] is not JsonArray tools)
            throw new InvalidDataException("The local MCP server did not return a tools list.");

        var converted = new JsonArray();
        foreach (var tool in tools)
        {
            if (tool is null || tool["name"]?.GetValue<string>() is not { Length: > 0 } name)
                continue;

            var fn = new JsonObject
            {
                ["name"] = name,
                ["description"] = tool["description"]?.GetValue<string>() ?? ""
            };
            fn["parameters"] = tool["inputSchema"]?.DeepClone() ?? new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            };
            converted.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = fn
            });
        }
        OpenAiTools = converted;
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        await _protocol.WaitAsync(ct);
        try
        {
            ThrowIfUnavailable();
            var id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            };
            await _process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);

            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct);
                if (line is null)
                    throw new EndOfStreamException("The local MCP server closed its output stream.");
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonObject? response;
                try
                {
                    response = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("The local MCP server returned invalid JSON-RPC data.", ex);
                }

                if (response is null || response["id"]?.GetValue<int>() != id)
                    continue; // Ignore notifications and any unrelated message without exposing stderr.

                if (response["error"] is { } error)
                {
                    var code = error["code"]?.ToJsonString() ?? "unknown";
                    throw new InvalidDataException($"The local MCP request '{method}' failed (code {code}).");
                }
                return response["result"] as JsonObject
                    ?? throw new InvalidDataException($"The local MCP request '{method}' returned no result.");
            }
        }
        finally
        {
            _protocol.Release();
        }
    }

    private async Task WriteNotificationAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        await _protocol.WaitAsync(ct);
        try
        {
            ThrowIfUnavailable();
            var notification = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters
            };
            await _process.StandardInput.WriteLineAsync(notification.ToJsonString().AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }
        finally
        {
            _protocol.Release();
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process.HasExited)
            throw new InvalidOperationException("The local MCP server exited before finishing the request.");
    }

    internal static ProcessStartInfo CreateStartInfo(string workingDirectory, AgentHarnessAuthorization authorization)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("Could not determine the current beacon executable path.");

        var start = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var executableName = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssembly) || !File.Exists(entryAssembly))
                throw new InvalidOperationException("Could not locate the Beacon CLI assembly for the MCP child process.");
            start.ArgumentList.Add(entryAssembly);
        }

        start.ArgumentList.Add("mcp");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(Path.GetFullPath(workingDirectory));

        var inherited = SafeInheritedEnvironment
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(pair => pair.Value is not null)
            .ToArray();
        start.Environment.Clear();
        foreach (var pair in inherited)
            start.Environment[pair.Name] = pair.Value!;

        start.Environment["BEACON_API_URL"] = authorization.ApiBaseUrl;
        start.Environment["BEACON_API_TOKEN"] = authorization.ApiToken;
        start.Environment["BEACON_PROJECT_ID"] = authorization.ProjectId.ToString("D");
        if (authorization.TaskId is { } taskId && taskId != Guid.Empty)
            start.Environment["BEACON_TASK_ID"] = taskId.ToString("D");
        if (authorization.SubtaskId is { } subtaskId && subtaskId != Guid.Empty)
            start.Environment["BEACON_SUBTASK_ID"] = subtaskId.ToString("D");
        if (!string.IsNullOrWhiteSpace(authorization.PipelineRole))
            start.Environment["BEACON_PIPELINE_ROLE"] = authorization.PipelineRole;
        return start;
    }

    private static async Task DrainStandardErrorAsync(Process process)
    {
        try
        {
            // Never forward child stderr to logs: provider/tool errors must not leak env values.
            while (await process.StandardError.ReadLineAsync() is not null) { }
        }
        catch { }
    }
}

internal interface IMcpToolClient : IAsyncDisposable
{
    JsonArray OpenAiTools { get; }
    Task<McpToolResult> CallToolAsync(string name, JsonObject arguments, CancellationToken ct);
}

internal sealed record McpToolResult(bool IsError, string Text);
