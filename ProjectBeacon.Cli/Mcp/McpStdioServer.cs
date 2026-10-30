namespace ProjectBeacon.Cli.Mcp;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Application.CodeIndex;
using Application.Mcp;
/// <summary>
/// Stdio MCP server. File tools stay inside --root. Every control-plane tool goes through the authenticated /v1 API; no direct database path is available.
/// </summary>
public static partial class McpStdioServer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Task<int> RunAsync(string root)
    {
        var env = McpEnvironment.Process;
        var resolved = BeaconApiClient.Resolve(env);
        Console.Error.WriteLine(resolved.Misconfigured
            ? "beacon mcp: both BEACON_API_URL and BEACON_API_TOKEN must be set, with a valid HTTP(S) URL."
            : resolved.Client is null
                ? "beacon mcp: control-plane tools require BEACON_API_URL and BEACON_API_TOKEN; file tools remain available."
                : "beacon mcp: authenticated API mode.");
        return RunAsync(root, Console.OpenStandardInput(), Console.OpenStandardOutput(), resolved.Client, env);
    }

    public static Task<int> RunAsync(string root, Stream input, Stream output)
    {
        return RunAsync(root, input, output, null, McpEnvironment.Empty);
    }

    internal static async Task<int> RunAsync(string root, Stream input, Stream output, BeaconApiClient? api, McpEnvironment env)
    {
        const int maxConcurrentRequests = 32;
        using var requestSlots = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
        using var outputGate = new SemaphoreSlim(1, 1);
        var pending = new List<Task>();

        while (true)
        {
            var message = await ReadMessageAsync(input);
            if (message is null)
                break;

            await requestSlots.WaitAsync();
            pending.Add(ProcessRequestAsync(message, root, output, api, env, outputGate, requestSlots));
        }

        await Task.WhenAll(pending);
        return 0;
    }

    private static async Task ProcessRequestAsync(
        JsonNode message,
        string root,
        Stream output,
        BeaconApiClient? api,
        McpEnvironment env,
        SemaphoreSlim outputGate,
        SemaphoreSlim requestSlots)
    {
        try
        {
            // Keep request-local workspace/index state so concurrent tools do not race over
            // mutable caches. MCP responses are correlated by id and can be written as soon
            // as each independent request completes.
            var response = await HandleAsync(
                message,
                new FileWorkspace(root),
                new CodeIndex(root),
                api,
                env);
            if (response is null)
                return;

            await outputGate.WaitAsync();
            try
            {
                await WriteMessageAsync(output, response);
            }
            finally
            {
                outputGate.Release();
            }
        }
        catch (Exception ex)
        {
            var id = message["id"]?.DeepClone();
            if (id is not null)
            {
                var error = Error(id, -32603, "Internal error: " + ex.Message);
                await outputGate.WaitAsync();
                try
                {
                    await WriteMessageAsync(output, error);
                }
                finally
                {
                    outputGate.Release();
                }
            }
        }
        finally
        {
            requestSlots.Release();
        }
    }

    private static async Task<JsonNode?> HandleAsync(JsonNode message, FileWorkspace workspace, CodeIndex index, BeaconApiClient? api, McpEnvironment env)
    {
        var method = message["method"]?.GetValue<string>();
        var id = message["id"];

        if (method is null)
            return id is null ? null : Error(id, -32600, "Invalid Request");

        if (method == "notifications/initialized" || method.StartsWith("notifications/", StringComparison.Ordinal))
            return null;

        if (id is null)
            return null;

        return method switch
        {
            "initialize" => Result(id, new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "beacon", ["version"] = AssemblyVersion() }
            }),
            "tools/list" => await ListToolsAsync(id, api, env),
            "tools/call" => await CallToolAsync(id, message["params"], workspace, index, api, env),
            "ping" => Result(id, new JsonObject()),
            _ => Error(id, -32601, $"Unknown method: {method}")
        };
    }

    private static JsonArray Tools()
    {
        var tools = new JsonArray();
        foreach (var tool in LocalTools())
            tools.Add(tool);
        foreach (var tool in SessionTools())
            tools.Add(tool);
        foreach (var tool in McpApiTools.Definitions())
            tools.Add(tool);
        return tools;
    }


    private static async Task<JsonObject> CallToolAsync(JsonNode id, JsonNode? args, FileWorkspace workspace, CodeIndex index, BeaconApiClient? api, McpEnvironment env)
    {
        var name = args?["name"]?.GetValue<string>();
        JsonObject? arguments = args?["arguments"] as JsonObject;
        if (arguments is null && args?["arguments"] is JsonValue raw && raw.GetValueKind() == JsonValueKind.String)
            arguments = JsonNode.Parse(raw.GetValue<string>() ?? "{}") as JsonObject;

        if (string.IsNullOrEmpty(name))
            return ToolError(id, "malformed path");

        var denied = await EnforceSubtaskScopeAsync(name, arguments, api, env);
        if (denied is not null)
            return ToolError(id, denied);

        try
        {
            if (IsControlPlaneTool(name) && RequireApi(id, api, env) is { } apiError)
                return apiError;

            return name switch
            {
                "read_file" => FileResult(id, workspace.ReadFile(Arg(arguments, "path"))),
                "write_file" => BoolResult(id, workspace.WriteFile(Arg(arguments, "path"), Arg(arguments, "content"))),
                "apply_patch" => BoolResult(id, workspace.ApplyPatch(
                    Arg(arguments, "path"),
                    Arg(arguments, "oldText"),
                    Arg(arguments, "newText"))),
                "get_tree" => TextResult(id, index.GetTree(
                    OptArg(arguments, "path"),
                    OptInt(arguments, "maxEntries") ?? CodeIndex.DefaultMaxEntries), FormatTree),
                "search_code" => TextResult(id, index.Search(
                    Arg(arguments, "query"),
                    SplitPrefixes(OptArg(arguments, "path")),
                    OptInt(arguments, "maxMatches") ?? CodeIndex.DefaultMaxMatches), FormatSearch),
                "get_changed_scope" => TextResult(id, index.GetChangedScope(
                    SplitPrefixes(OptArg(arguments, "path")),
                    OptInt(arguments, "maxFiles") ?? CodeIndex.DefaultMaxFiles), FormatFileList),
                "get_signatures" => TextResult(id, index.GetSignatures(
                    OptArg(arguments, "path") is { Length: > 0 } p ? SplitPrefixes(p) : null,
                    OptInt(arguments, "maxFiles") ?? CodeIndex.DefaultMaxFiles), FormatSignatures),
                "get_callers" => TextResult(id, index.GetCallers(
                    Arg(arguments, "path"),
                    Arg(arguments, "symbolName"),
                    OptInt(arguments, "line") ?? 1), FormatCallers),
                "hash_range" => TextResult(id, index.HashRange(
                    Arg(arguments, "path"),
                    OptInt(arguments, "startLine") ?? 0,
                    OptInt(arguments, "endLine") ?? 0), h => h),
                "model_bind" => await ModelBindAsync(id, arguments, api, env),
                "model_status" => await ModelStatusAsync(id, api, env),
                "task_create_subtask" => await TaskCreateSubtaskAsync(id, arguments, api, env),
                "subtask_report_result" => await SubtaskReportResultAsync(id, arguments, api, env),
                "task_review_verdict" => await TaskReviewVerdictAsync(id, arguments, api, env),
                "task_pipeline_status" => await TaskPipelineStatusAsync(id, api, env),
                "context_compile" => api is not null ? await ContextCompileAsync(id, workspace, index, api, env) : await ApiTextAsync(id, await McpApiTools.CallAsync(name, arguments, api, env)),
                _ when McpApiTools.IsApiTool(name) => await ApiTextAsync(id, await McpApiTools.CallAsync(name, arguments, api, env)),
                _ => ToolError(id, $"Unknown tool: {name}")
            };
        }
        catch (ArgumentException ex)
        {
            return ToolError(id, ex.Message);
        }
    }

    private static bool IsControlPlaneTool(string name) =>
        name is "model_bind" or "model_status" or "task_create_subtask"
            or "subtask_report_result" or "task_review_verdict" or "task_pipeline_status" or "context_compile"
        || McpApiTools.IsApiTool(name);

    private static async Task<string?> EnforceSubtaskScopeAsync(string tool, JsonObject? arguments, BeaconApiClient? api, McpEnvironment env)
    {
        var roleError = ValidatePipelineRoleTool(tool, env);
        if (roleError is not null)
            return roleError;

        var role = env.Get("BEACON_PIPELINE_ROLE");
        if (string.Equals(role, "Planner", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Review", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(role, "Eval", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase))
        {
            if (api is null
                || !TryParseScope(env, out _, out var actorTaskId, out _)
                || actorTaskId is null)
                return "pipeline scope could not be loaded.";

            if (IsActorTerminalTool(tool))
            {
                if (!Guid.TryParse(env.Get("BEACON_SUBTASK_ID"), out var ownedSubtaskId)
                    || ownedSubtaskId == Guid.Empty)
                    return "Actor pipeline sessions require an explicit subtask scope.";
                if (!Guid.TryParse(OptArg(arguments, "subtaskId"), out var requestedSubtaskId)
                    || requestedSubtaskId != ownedSubtaskId)
                    return "Actor lifecycle tools can only report the current subtask.";
            }
            else if (McpApiTools.IsApiTool(tool))
            {
                return $"Tool '{tool}' is not allowed for the Actor pipeline role.";
            }
        }

        if (api is null || !TryParseScope(env, out _, out var taskId, out _) || taskId is null)
            return null;

        var (ok, _, body) = await api.SendAsync(HttpMethod.Get, $"v1/tasks/{taskId.Value:D}/pipeline", null, CancellationToken.None);
        if (!ok)
            return "pipeline scope could not be loaded.";

        Guid? explicitId = null;
        var fromEnv = env.Get("BEACON_SUBTASK_ID");
        if (string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase))
        {
            // Never let a tool argument pick the Actor's security scope. It must come
            // from the immutable authorization created for this pipeline command.
            if (!Guid.TryParse(fromEnv, out var actorSubtaskId) || actorSubtaskId == Guid.Empty)
                return "Actor pipeline sessions require an explicit subtask scope.";
            explicitId = actorSubtaskId;
        }
        else if (Guid.TryParse(fromEnv, out var envId))
        {
            explicitId = envId;
        }
        else if (Guid.TryParse(OptArg(arguments, "subtaskId"), out var argId))
        {
            explicitId = argId;
        }

        var paths = SubtaskScopeGuard.UsesPath(tool) ? SplitPrefixes(OptArg(arguments, "path")) : null;
        var allowlists = SubtaskScopeGuard.ParsePipeline(body);
        if (string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase))
        {
            if (explicitId is not { } actorSubtaskId)
                return "Actor pipeline sessions require an explicit subtask scope.";

            var actorSubtask = allowlists.FirstOrDefault(s => s.Id == actorSubtaskId);
            if (actorSubtask.Id == Guid.Empty)
                return "subtask not found";
            if (!string.Equals(actorSubtask.Status, "InProgress", StringComparison.OrdinalIgnoreCase))
                return "Actor subtask is no longer active.";
        }

        var rejected = SubtaskScopeGuard.Reject(tool, paths, allowlists, explicitId);
        if (rejected is not null)
            return rejected;

        // For pipeline Actors, an empty AllowedMcpTools list must deny workspace tools,
        // not inherit the generic MCP client's legacy "empty means unrestricted" behavior.
        if (string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase)
            && !IsActorTerminalTool(tool)
            && !McpApiTools.IsApiTool(tool))
            return RejectActorToolGrant(tool, allowlists, explicitId);

        return null;
    }

    private static JsonObject Content(string text) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text }
        }
    };

    private static JsonObject ToolError(JsonNode id, string message) =>
        Result(id, new JsonObject
        {
            ["isError"] = true,
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = message }
            }
        });


    private static JsonObject Tool(string name, string description, JsonObject schema) => McpSchema.Tool(name, description, schema);

    private static JsonObject Props(params (string Name, string Type, bool Required)[] fields) => McpSchema.Props(fields);

    private static JsonObject Result(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result
    };

    private static JsonObject Error(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private static async Task<JsonNode?> ReadMessageAsync(Stream stream)
    {
        while (true)
        {
            var line = await ReadUtf8LineAsync(stream);
            if (line is null)
                return null;
            if (line.Length == 0)
                continue;
            return JsonNode.Parse(line);
        }
    }

    private static async Task<string?> ReadUtf8LineAsync(Stream stream)
    {
        var bytes = new List<byte>(256);
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (b == '\n')
                break;
            if (b != '\r')
                bytes.Add((byte)b);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task WriteMessageAsync(Stream stream, JsonNode message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString(Json) + "\n");
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static string AssemblyVersion() =>
        typeof(McpStdioServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(McpStdioServer).Assembly.GetName().Version?.ToString()
        ?? "0";
}