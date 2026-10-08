namespace ProjectBeacon.Cli;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Application;
using Application.Agents;
using Application.CodeIndex;
using Application.Mcp;
using Application.Tasks;
using Domain.Enums;
using Infrastructure;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Cli.Mcp;
/// <summary>
/// Stdio MCP server. File tools stay inside --root. Pipeline and model tools use the API when both BEACON_API_URL and BEACON_API_TOKEN are set, the database when neither is set, and fail when only one is set.
/// </summary>
public static partial class McpStdioServer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions JsonDb = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly object DbLock = new();
    private static ServiceProvider? _dbProvider;

    private static bool _apiMisconfigured;

    public static Task<int> RunAsync(string root)
    {
        var env = McpEnvironment.Process;
        var resolved = BeaconApiClient.Resolve(env);
        _apiMisconfigured = resolved.Misconfigured;
        Console.Error.WriteLine(resolved.Misconfigured
            ? "beacon mcp: BEACON_API_URL and BEACON_API_TOKEN must both be set, or both be unset."
            : resolved.Client is null
                ? "beacon mcp: local database mode."
                : "beacon mcp: API mode.");
        return RunAsync(root, Console.OpenStandardInput(), Console.OpenStandardOutput(), resolved.Client, env);
    }

    public static Task<int> RunAsync(string root, Stream input, Stream output)
    {
        _apiMisconfigured = false;
        return RunAsync(root, input, output, null, McpEnvironment.Empty);
    }

    internal static async Task<int> RunAsync(string root, Stream input, Stream output, BeaconApiClient? api, McpEnvironment env)
    {
        var workspace = new FileWorkspace(root);
        var index = new CodeIndex(root);

        while (true)
        {
            var message = await ReadMessageAsync(input);
            if (message is null)
                return 0;

            var response = await HandleAsync(message, workspace, index, api, env);
            if (response is not null)
                await WriteMessageAsync(output, response);
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
            "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
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

    private static async Task<string?> EnforceSubtaskScopeAsync(string tool, JsonObject? arguments, BeaconApiClient? api, McpEnvironment env)
    {
        if (api is null || !TryParseScope(env, out _, out var taskId, out _) || taskId is null)
            return null;

        var (ok, _, body) = await api.SendAsync(HttpMethod.Get, $"v1/tasks/{taskId.Value:D}/pipeline", null, CancellationToken.None);
        if (!ok)
            return "pipeline scope could not be loaded.";

        Guid? explicitId = null;
        var fromEnv = env.Get("BEACON_SUBTASK_ID");
        if (Guid.TryParse(fromEnv, out var envId))
            explicitId = envId;
        else if (Guid.TryParse(OptArg(arguments, "subtaskId"), out var argId))
            explicitId = argId;

        var paths = SubtaskScopeGuard.UsesPath(tool) ? SplitPrefixes(OptArg(arguments, "path")) : null;
        return SubtaskScopeGuard.Reject(tool, paths, SubtaskScopeGuard.ParsePipeline(body), explicitId);
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