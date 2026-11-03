namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;

/// <summary>
/// Starts project-configured MCP servers beside the workstation harness and merges their tools
/// into one collision-safe OpenAI tool catalog. A failing optional server does not disable the
/// built-in Beacon MCP server or other configured servers.
/// </summary>
internal static class ProjectMcpToolClient
{
    public static async Task<IMcpToolClient> StartAsync(
        AgentHarnessAuthorization authorization,
        Action<string>? log,
        CancellationToken ct)
    {
        var beacon = await McpStdioClient.StartAsync(authorization, ct);
        var clients = new List<(string Name, IMcpToolClient Client)>();
        var aliases = new Dictionary<string, (IMcpToolClient Client, string OriginalName)>(StringComparer.Ordinal);
        var tools = new JsonArray();

        foreach (var tool in beacon.OpenAiTools)
            tools.Add(tool?.DeepClone());

        var interactive = string.IsNullOrWhiteSpace(authorization.PipelineRole);
        var actorPipeline = string.Equals(authorization.PipelineRole, "Actor", StringComparison.OrdinalIgnoreCase);

        if (interactive || actorPipeline)
        {
            foreach (var server in authorization.McpServers ?? [])
            {
                ct.ThrowIfCancellationRequested();
                if (!server.Enabled)
                    continue;

                if (actorPipeline)
                {
                    var serverPrefix = $"mcp_{server.Name.ToLowerInvariant()}_";
                    if (authorization.AllowedMcpTools?.Any(allowed =>
                        allowed.StartsWith(serverPrefix, StringComparison.OrdinalIgnoreCase)) != true)
                        continue;
                }

                IMcpToolClient? client = null;
                try
                {
                    client = server.Transport switch
                    {
                        "stdio" => await ExternalMcpStdioClient.StartAsync(server, authorization.WorkingDirectory, ct),
                        "streamable-http" => await McpStreamableHttpClient.StartAsync(server, ct),
                        _ => throw new InvalidOperationException("Unsupported MCP transport.")
                    };

                    var serverTools = 0;
                    foreach (var rawTool in client.OpenAiTools)
                    {
                        if (rawTool?["function"] is not JsonObject function
                            || function["name"]?.GetValue<string>() is not { Length: > 0 } originalName)
                            continue;

                        var alias = CreateToolAlias(server.Name, originalName);
                        if (actorPipeline
                            && authorization.AllowedMcpTools?.Any(allowed =>
                                string.Equals(allowed, alias, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(allowed, $"mcp_{server.Name}_*", StringComparison.OrdinalIgnoreCase)) != true)
                            continue;
                        if (aliases.ContainsKey(alias))
                            continue;

                        var definition = rawTool!.DeepClone() as JsonObject;
                        if (definition?["function"] is JsonObject clonedFunction)
                            clonedFunction["name"] = alias;
                        tools.Add(definition);
                        aliases.Add(alias, (client, originalName));
                        serverTools++;
                    }

                    if (serverTools == 0 && actorPipeline)
                    {
                        await client.DisposeAsync();
                        client = null;
                        continue;
                    }

                    clients.Add((server.Name, client));
                    log?.Invoke($"MCP server '{server.Name}' connected ({serverTools} tools).");
                    client = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    if (client is not null)
                        await client.DisposeAsync();
                    await DisposeAllAsync(clients);
                    await beacon.DisposeAsync();
                    throw;
                }
                catch (Exception ex)
                {
                    if (client is not null)
                        await client.DisposeAsync();
                    // Do not include exception messages: child processes and HTTP libraries may
                    // echo command arguments or credential-bearing request details.
                    log?.Invoke($"MCP server '{server.Name}' could not be started: {DescribeStartFailure(ex, server)}");
                }
            }
        }

        return new CompositeMcpToolClient(beacon, clients, aliases, tools);
    }

    private static string CreateToolAlias(string serverName, string toolName)
    {
        var server = new string(serverName.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_')
            .ToArray());
        var original = new string(toolName
            .Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' || character == '-' ? character : '_')
            .ToArray());
        var prefix = $"mcp_{server}_";
        if (prefix.Length + original.Length <= 64)
            return prefix + original;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverName + "\0" + toolName)))[..8].ToLowerInvariant();
        var available = Math.Max(1, 64 - prefix.Length - hash.Length - 1);
        return prefix + original[..Math.Min(original.Length, available)] + "_" + hash;
    }

    private static async Task DisposeAllAsync(IEnumerable<(string Name, IMcpToolClient Client)> clients)
    {
        foreach (var (_, client) in clients)
        {
            try { await client.DisposeAsync(); } catch { }
        }
    }

    private sealed class CompositeMcpToolClient(
        IMcpToolClient beacon,
        IReadOnlyList<(string Name, IMcpToolClient Client)> clients,
        IReadOnlyDictionary<string, (IMcpToolClient Client, string OriginalName)> aliases,
        JsonArray tools) : IMcpToolClient
    {
        public JsonArray OpenAiTools { get; } = tools;

        public Task<McpToolResult> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
        {
            if (aliases.TryGetValue(name, out var entry))
                return entry.Client.CallToolAsync(entry.OriginalName, arguments, ct);
            return beacon.CallToolAsync(name, arguments, ct);
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAllAsync(clients);
            await beacon.DisposeAsync();
        }
    }
}

/// <summary>Generic external MCP stdio client. It never receives Beacon's API token.</summary>
internal sealed class ExternalMcpStdioClient : IMcpToolClient
{
    private const int MaxResultCharacters = 16_000;
    private static readonly string[] SafeEnvironment =
    [
        "PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "TEMP", "TMP", "TMPDIR", "SystemRoot", "WINDIR",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_CLI_HOME", "DOTNET_MULTILEVEL_LOOKUP",
        "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH"
    ];

    private readonly Process _process;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private int _nextId;
    private bool _disposed;

    private ExternalMcpStdioClient(Process process) => _process = process;

    public JsonArray OpenAiTools { get; private set; } = [];

    public static async Task<ExternalMcpStdioClient> StartAsync(
        McpServerConfiguration config, string workingDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
            throw new InvalidOperationException("The MCP executable is missing.");

        var arguments = config.Arguments ?? [];
        var batchCommand = OperatingSystem.IsWindows()
            ? ResolveWindowsBatchCommand(config.Command.Trim())
            : null;
        var start = new ProcessStartInfo
        {
            FileName = batchCommand is null
                ? config.Command
                : Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (batchCommand is null)
        {
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
        }
        else
        {
            // npx/npm are commonly installed as .cmd shims on Windows. Run only these explicit
            // batch files through cmd.exe, with every argument quoted and shell expansion disabled.
            if (batchCommand.Contains('"') || batchCommand.Contains('%')
                || arguments.Any(argument => argument.Any(character =>
                    character is '"' or '%' or '\r' or '\n' or '\0')))
            {
                throw new InvalidOperationException(
                    "Windows .cmd MCP arguments cannot contain quotes, percent signs or line breaks. Use a native executable for those arguments.");
            }

            var commandLine = $"\"{batchCommand}\""
                + string.Concat(arguments.Select(argument => $" \"{argument}\""));
            start.Arguments = $"/d /v:off /s /c \"{commandLine}\"";
        }

        var inherited = SafeEnvironment
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(pair => pair.Value is not null)
            .ToArray();
        start.Environment.Clear();
        foreach (var pair in inherited)
            start.Environment[pair.Name] = pair.Value!;
        foreach (var pair in config.Environment ?? new Dictionary<string, string>())
            start.Environment[pair.Key] = pair.Value;

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The MCP process did not start.");
            var client = new ExternalMcpStdioClient(process);
            _ = DrainErrorAsync(process);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            await client.InitializeAsync(startup.Token);
            return client;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
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
        return McpResponse.ReadToolResult(response, MaxResultCharacters);
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var initialized = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "projectbeacon-native", ["version"] = "1" }
        }, ct);
        if (initialized["protocolVersion"]?.GetValue<string>() is not { Length: > 0 })
            throw new InvalidDataException("The MCP server did not complete initialization.");

        await WriteNotificationAsync("notifications/initialized", new JsonObject(), ct);
        var response = await RequestAsync("tools/list", new JsonObject(), ct);
        if (response["tools"] is not JsonArray tools)
            throw new InvalidDataException("The MCP server did not return a tools list.");

        OpenAiTools = McpResponse.ToOpenAiTools(tools);
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        await _requestLock.WaitAsync(ct);
        try
        {
            ThrowIfUnavailable();
            var id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters
            };
            await _process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);

            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct);
                if (line is null)
                    throw new EndOfStreamException("The MCP process closed its output stream.");
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                JsonObject? response;
                try { response = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (response is null || response["id"] is null)
                    continue;
                if (response["id"]!.ToJsonString() != JsonValue.Create(id)!.ToJsonString())
                    continue;
                if (response["error"] is JsonObject error)
                    throw new InvalidOperationException(error["message"]?.GetValue<string>() ?? "The MCP request failed.");
                return response["result"] as JsonObject
                    ?? throw new InvalidDataException("The MCP server returned an invalid response.");
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task WriteNotificationAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters
        };
        await _process.StandardInput.WriteLineAsync(notification.ToJsonString().AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ExternalMcpStdioClient));
        if (_process.HasExited)
            throw new InvalidOperationException("The MCP process exited unexpectedly.");
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
        _requestLock.Dispose();
    }

    private static string? ResolveWindowsBatchCommand(string command)
    {
        var extension = Path.GetExtension(command);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            return command;
        if (extension.Length > 0)
            return null;

        var suffixes = new[] { ".cmd", ".bat" };
        var hasDirectory = command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar);
        var directories = hasDirectory
            ? new[] { Path.GetDirectoryName(Path.GetFullPath(command)) ?? Environment.CurrentDirectory }
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        foreach (var suffix in suffixes)
        {
            var candidate = hasDirectory
                ? command + suffix
                : Path.Combine(directory, command + suffix);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static string DescribeStartFailure(Exception exception, McpServerConfiguration config)
    {
        var message = exception.Message;
        var secrets = new List<string>();
        if (config.Environment is not null)
            secrets.AddRange(config.Environment.Values);
        if (config.Headers is not null)
            secrets.AddRange(config.Headers.Values);
        if (!string.IsNullOrWhiteSpace(config.Endpoint))
            secrets.Add(config.Endpoint);

        foreach (var secret in secrets.Where(value => !string.IsNullOrEmpty(value)))
            message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        if (message.Length > 240)
            message = message[..240] + "…";
        return $"{exception.GetType().Name}: {message}";
    }

    private static async Task DrainErrorAsync(Process process)
    {
        try { while (await process.StandardError.ReadLineAsync() is not null) { } } catch { }
    }
}

/// <summary>MCP Streamable HTTP client (JSON or server-sent-event responses).</summary>
internal sealed class McpStreamableHttpClient : IMcpToolClient
{
    private const string ProtocolVersion = "2024-11-05";
    private const int MaxResultCharacters = 16_000;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private string? _sessionId;
    private int _nextId;
    private bool _disposed;

    private McpStreamableHttpClient(McpServerConfiguration config)
    {
        _endpoint = new Uri(config.Endpoint!, UriKind.Absolute);
        _headers = config.Headers ?? new Dictionary<string, string>();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public JsonArray OpenAiTools { get; private set; } = [];

    public static async Task<McpStreamableHttpClient> StartAsync(McpServerConfiguration config, CancellationToken ct)
    {
        var client = new McpStreamableHttpClient(config);
        try
        {
            var initialized = await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "projectbeacon-native", ["version"] = "1" }
            }, ct, includeProtocolHeader: false);
            if (initialized?["protocolVersion"]?.GetValue<string>() is not { Length: > 0 })
                throw new InvalidDataException("The remote MCP server did not complete initialization.");

            await client.NotifyAsync("notifications/initialized", new JsonObject(), ct);
            var listed = await client.RequestAsync("tools/list", new JsonObject(), ct);
            if (listed?["tools"] is not JsonArray tools)
                throw new InvalidDataException("The remote MCP server did not return a tools list.");
            client.OpenAiTools = McpResponse.ToOpenAiTools(tools);
            return client;
        }
        catch
        {
            client.Dispose();
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
        return response is null
            ? new McpToolResult(true, "The remote MCP server returned an empty response.")
            : McpResponse.ReadToolResult(response, MaxResultCharacters);
    }

    private async Task<JsonObject?> RequestAsync(
        string method, JsonObject parameters, CancellationToken ct, bool includeProtocolHeader = true)
    {
        await _requestLock.WaitAsync(ct);
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(McpStreamableHttpClient));
            var id = Interlocked.Increment(ref _nextId);
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters
            };
            return await PostAsync(body, id, ct, includeProtocolHeader);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task NotifyAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
        using var response = await SendAsync(body, ct, includeProtocolHeader: true);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Remote MCP notification failed with HTTP {(int)response.StatusCode}.");
    }

    private async Task<JsonObject?> PostAsync(JsonObject body, int id, CancellationToken ct, bool includeProtocolHeader)
    {
        using var response = await SendAsync(body, ct, includeProtocolHeader);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Remote MCP request failed with HTTP {(int)response.StatusCode}.");
        CaptureSession(response);
        if (response.StatusCode == HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0)
            return null;

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                var data = line[5..].Trim();
                if (data.Length == 0 || data == "[DONE]")
                    continue;
                JsonObject? message;
                try { message = JsonNode.Parse(data) as JsonObject; }
                catch (JsonException) { continue; }
                if (message is null || message["id"]?.ToJsonString() != JsonValue.Create(id)!.ToJsonString())
                    continue;
                if (message["error"] is JsonObject error)
                    throw new InvalidOperationException(error["message"]?.GetValue<string>() ?? "The remote MCP request failed.");
                return message["result"] as JsonObject
                    ?? throw new InvalidDataException("The remote MCP server returned an invalid response.");
            }
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var parsed = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("The remote MCP server returned an invalid JSON response.");
        if (parsed["error"] is JsonObject rpcError)
            throw new InvalidOperationException(rpcError["message"]?.GetValue<string>() ?? "The remote MCP request failed.");
        return parsed["result"] as JsonObject
            ?? throw new InvalidDataException("The remote MCP server returned an invalid response.");
    }

    private async Task<HttpResponseMessage> SendAsync(JsonObject body, CancellationToken ct, bool includeProtocolHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (includeProtocolHeader)
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        if (!string.IsNullOrWhiteSpace(_sessionId))
            request.Headers.TryAddWithoutValidation("MCP-Session-Id", _sessionId);
        foreach (var header in _headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        finally
        {
            request.Dispose();
        }
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            _sessionId = values.FirstOrDefault();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
        _requestLock.Dispose();
    }
}

internal static class McpResponse
{
    public static JsonArray ToOpenAiTools(JsonArray tools)
    {
        var converted = new JsonArray();
        foreach (var tool in tools)
        {
            if (tool is null || tool["name"]?.GetValue<string>() is not { Length: > 0 } name)
                continue;
            converted.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = tool["description"]?.GetValue<string>() ?? string.Empty,
                    ["parameters"] = tool["inputSchema"]?.DeepClone() ?? new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject()
                    }
                }
            });
        }
        return converted;
    }

    public static McpToolResult ReadToolResult(JsonObject response, int maxCharacters)
    {
        var isError = response["isError"]?.GetValue<bool>() == true;
        var output = new StringBuilder();
        if (response["content"] is JsonArray content)
        {
            foreach (var item in content)
            {
                if (item is null)
                    continue;
                if (output.Length > 0)
                    output.Append('\n');
                if (item["type"]?.GetValue<string>() == "text"
                    && item["text"]?.GetValue<string>() is { } text)
                    output.Append(text);
                else
                    output.Append(item.ToJsonString());

                if (output.Length > maxCharacters)
                {
                    output.Length = maxCharacters;
                    output.Append("\n[tool result truncated by ProjectBeacon]");
                    break;
                }
            }
        }
        if (output.Length == 0 && response["structuredContent"] is { } structured)
            output.Append(structured.ToJsonString());
        return new McpToolResult(isError, output.ToString());
    }
}
