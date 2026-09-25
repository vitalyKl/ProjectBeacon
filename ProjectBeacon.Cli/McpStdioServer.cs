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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Cli.Mcp;

public static class McpStdioServer
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

    public static Task<int> RunAsync(string root)
        => RunAsync(root, Console.OpenStandardInput(), Console.OpenStandardOutput(), BeaconApiClient.FromEnvironment());

    public static Task<int> RunAsync(string root, Stream input, Stream output)
        => RunAsync(root, input, output, null);

    internal static async Task<int> RunAsync(string root, Stream input, Stream output, BeaconApiClient? api)
    {
        var workspace = new FileWorkspace(root);
        var index = new CodeIndex(root);

        while (true)
        {
            var message = await ReadMessageAsync(input);
            if (message is null)
                return 0;

            var response = await HandleAsync(message, workspace, index, api);
            if (response is not null)
                await WriteMessageAsync(output, response);
        }
    }

    private static async Task<JsonNode?> HandleAsync(JsonNode message, FileWorkspace workspace, CodeIndex index, BeaconApiClient? api)
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
            "tools/call" => await CallToolAsync(id, message["params"], workspace, index, api),
            "ping" => Result(id, new JsonObject()),
            _ => Error(id, -32601, $"Unknown method: {method}")
        };
    }

    private static JsonArray Tools()
    {
        var tools = LocalTools();
        foreach (var tool in McpApiTools.Definitions())
            tools.Add(tool);
        return tools;
    }

    private static JsonArray LocalTools() =>
    [
        Tool("read_file", "Read a file inside the project root.",
            Props(("path", "string", true))),
        Tool("write_file", "Write a file inside the project root. Rejects path escape.",
            Props(("path", "string", true), ("content", "string", true))),
        Tool("apply_patch", "Replace one exact occurrence of oldText with newText in a file.",
            Props(("path", "string", true), ("oldText", "string", true), ("newText", "string", true))),
        Tool("get_tree", "List files and directories under the project root or a sub-path. Re-scans the working tree on every call.",
            Props(("path", "string", false), ("maxEntries", "integer", false))),
        Tool("search_code", "Literal substring search over text files in the working tree.",
            Props(("query", "string", true), ("path", "string", false), ("maxMatches", "integer", false))),
        Tool("get_changed_scope", "List working-tree changed files from git status, optionally filtered by a path prefix.",
            Props(("path", "string", false), ("maxFiles", "integer", false))),
        Tool("model_bind", "Bind a pipeline role (planner, actor, review) to a local model backend. Requires BEACON_PROJECT_ID.",
            Props(("role", "string", true), ("modelBackendId", "string", true))),
        Tool("model_status", "Show local model backends, role bindings and llama-swap proxy status. Requires BEACON_PROJECT_ID.",
            Props()),
        Tool("task_create_subtask", "Create a subtask on the session task. Requires BEACON_PROJECT_ID and BEACON_TASK_ID.",
            Props(("instructions", "string", true), ("allowedMcpTools", "string", false), ("allowedPaths", "string", false))),
        Tool("subtask_report_result", "Report the result of an in-progress subtask on the session task. Requires BEACON_PROJECT_ID and BEACON_TASK_ID.",
            Props(("subtaskId", "string", true), ("diffRef", "string", true), ("summary", "string", true))),
        Tool("task_review_verdict", "Record a review verdict (approve, reopen_subtask) on the session task. Requires BEACON_PROJECT_ID and BEACON_TASK_ID.",
            Props(("verdict", "string", true), ("note", "string", true), ("subtaskId", "string", false))),
        Tool("task_pipeline_status", "Show the pipeline state (subtasks, sessions, verdicts) of the session task. Requires BEACON_PROJECT_ID and BEACON_TASK_ID.",
            Props())
    ];

    private static async Task<JsonObject> CallToolAsync(JsonNode id, JsonNode? args, FileWorkspace workspace, CodeIndex index, BeaconApiClient? api)
    {
        var name = args?["name"]?.GetValue<string>();
        JsonObject? arguments = args?["arguments"] as JsonObject;
        if (arguments is null && args?["arguments"] is JsonValue raw && raw.GetValueKind() == JsonValueKind.String)
            arguments = JsonNode.Parse(raw.GetValue<string>() ?? "{}") as JsonObject;

        if (string.IsNullOrEmpty(name))
            return ToolError(id, "malformed path");

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
                "model_bind" => await ModelBindAsync(id, arguments, api),
                "model_status" => await ModelStatusAsync(id, api),
                "task_create_subtask" => await TaskCreateSubtaskAsync(id, arguments, api),
                "subtask_report_result" => await SubtaskReportResultAsync(id, arguments, api),
                "task_review_verdict" => await TaskReviewVerdictAsync(id, arguments, api),
                "task_pipeline_status" => await TaskPipelineStatusAsync(id, api),
                _ when McpApiTools.IsApiTool(name) => await ApiTextAsync(id, await McpApiTools.CallAsync(name, arguments, api)),
                _ => ToolError(id, $"Unknown tool: {name}")
            };
        }
        catch (ArgumentException ex)
        {
            return ToolError(id, ex.Message);
        }
    }

    private readonly record struct McpScope(IServiceProvider Services, Guid ProjectId, Guid? TaskId);

    private static ServiceProvider GetDbServiceProvider()
    {
        lock (DbLock)
        {
            if (_dbProvider is null)
            {
                EnvFile.Load();
                var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
                var connectionString = PostgresConnection.Resolve(configuration);
                _dbProvider = new ServiceCollection()
                    .AddInfrastructure(connectionString)
                    .AddApplicationHandlers()
                    .BuildServiceProvider();
            }
            return _dbProvider;
        }
    }

    private static async Task<JsonObject> WithDbAsync(JsonNode id, bool requiresTask, Func<McpScope, Task<JsonObject>> invoke)
    {
        if (!TryParseScope(out var projectId, out var taskId, out var error))
            return ToolError(id, error!);
        if (requiresTask && taskId is null)
            return ToolError(id, "BEACON_TASK_ID is not set. Set it to the task GUID for this MCP session.");

        ServiceProvider provider;
        try
        {
            provider = GetDbServiceProvider();
        }
        catch (Exception ex)
        {
            return ToolError(id, $"database is not configured: {ex.Message}");
        }

        using var scope = provider.CreateScope();
        using var _ = TenantScope.EnterProjectScope(projectId);
        try
        {
            return await invoke(new McpScope(scope.ServiceProvider, projectId, taskId));
        }
        catch (Exception ex)
        {
            return ToolError(id, ex.Message);
        }
    }

    private static bool TryParseScope(out Guid projectId, out Guid? taskId, out string? error)
    {
        projectId = Guid.Empty;
        taskId = null;
        var projectRaw = Environment.GetEnvironmentVariable("BEACON_PROJECT_ID");
        if (string.IsNullOrWhiteSpace(projectRaw) || !Guid.TryParse(projectRaw, out projectId))
        {
            error = "BEACON_PROJECT_ID is not set (or is not a GUID). Set it to the project GUID for this MCP session.";
            return false;
        }

        var taskRaw = Environment.GetEnvironmentVariable("BEACON_TASK_ID");
        if (string.IsNullOrWhiteSpace(taskRaw))
        {
            taskId = null;
            error = null;
            return true;
        }
        if (!Guid.TryParse(taskRaw, out var task))
        {
            taskId = null;
            error = "BEACON_TASK_ID is set but is not a GUID.";
            return false;
        }
        taskId = task;
        error = null;
        return true;
    }

    private static JsonObject DbResult<T>(JsonNode id, Application.Common.Result<T> result)
        => result.Success
            ? Result(id, Content(JsonSerializer.Serialize(result.Value!, JsonDb)))
            : ToolError(id, result.Error ?? "error");

    private static PipelineRole? ParseRole(JsonObject? args)
    {
        var raw = OptArg(args, "role");
        if (string.Equals(raw, "planner", StringComparison.OrdinalIgnoreCase))
            return PipelineRole.Planner;
        if (string.Equals(raw, "actor", StringComparison.OrdinalIgnoreCase))
            return PipelineRole.Actor;
        if (string.Equals(raw, "review", StringComparison.OrdinalIgnoreCase))
            return PipelineRole.Review;
        return null;
    }

    private static ReviewVerdictKind? ParseVerdict(JsonObject? args)
    {
        var raw = OptArg(args, "verdict");
        if (string.Equals(raw, "approve", StringComparison.OrdinalIgnoreCase))
            return ReviewVerdictKind.Approve;
        if (string.Equals(raw, "reopen_subtask", StringComparison.OrdinalIgnoreCase))
            return ReviewVerdictKind.ReopenSubtask;
        return null;
    }

    private static Guid? ParseGuid(JsonObject? args, string key)
    {
        var raw = OptArg(args, key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return Guid.TryParse(raw, out var value) ? value : (Guid?)null;
    }

    private static async Task<JsonObject> ApiTextAsync(JsonNode id, McpToolText text)
        => text.IsError ? ToolError(id, text.Text) : Result(id, Content(text.Text));

    private static async Task<JsonObject> ModelBindAsync(JsonNode id, JsonObject? args, BeaconApiClient? api)
    {
        var role = ParseRole(args);
        if (role is null)
            return ToolError(id, "missing or invalid 'role' (expected planner, actor or review)");
        var modelId = ParseGuid(args, "modelBackendId");
        if (modelId is null)
            return ToolError(id, "missing or invalid 'modelBackendId' (expected a GUID)");

        if (api is not null)
        {
            if (!TryParseScope(out _, out _, out var error))
                return ToolError(id, error!);
            return await ApiTextAsync(id, await McpApiTools.BindRoleAsync(role.Value, modelId.Value, api));
        }

        return await WithDbAsync(id, requiresTask: false, async scope =>
        {
            var handler = scope.Services.GetRequiredService<SetRoleBindingHandler>();
            var result = await handler.HandleAsync(new SetRoleBindingCommand(new SetRoleBindingRequest(role.Value, modelId.Value)));
            return DbResult(id, result);
        });
    }

    private static async Task<JsonObject> ModelStatusAsync(JsonNode id, BeaconApiClient? api)
    {
        if (api is not null)
        {
            if (!TryParseScope(out _, out _, out var error))
                return ToolError(id, error!);
            return await ApiTextAsync(id, await McpApiTools.ModelStatusAsync(api));
        }

        return await WithDbAsync(id, requiresTask: false, async scope =>
        {
            var registry = await scope.Services
                .GetRequiredService<GetModelRegistryHandler>()
                .HandleAsync(new GetModelRegistryCommand());
            if (!registry.Success)
                return ToolError(id, registry.Error ?? "error");

            var proxy = await scope.Services
                .GetRequiredService<GetProxyStatusHandler>()
                .HandleAsync(new GetProxyStatusCommand());

            var status = new JsonObject
            {
                ["projectId"] = registry.Value!.ProjectId,
                ["backends"] = JsonSerializer.SerializeToNode(registry.Value.Backends, JsonDb) ?? new JsonArray(),
                ["bindings"] = JsonSerializer.SerializeToNode(registry.Value.Bindings, JsonDb) ?? new JsonArray(),
                ["proxy"] = proxy.Success
                    ? JsonSerializer.SerializeToNode(proxy.Value, JsonDb)
                    : new JsonObject { ["available"] = false, ["error"] = proxy.Error ?? "unknown proxy error" }
            };
            return Result(id, Content(status.ToJsonString()));
        });
    }

    private static async Task<JsonObject> TaskCreateSubtaskAsync(JsonNode id, JsonObject? args, BeaconApiClient? api)
    {
        var instructions = OptArg(args, "instructions");
        if (string.IsNullOrWhiteSpace(instructions))
            return ToolError(id, "missing 'instructions'");

        if (api is not null)
        {
            if (!TrySessionTask(out var taskId, out var error))
                return ToolError(id, error);
            return await ApiTextAsync(id, await McpApiTools.CreateSubtaskAsync(
                taskId, instructions, SplitPrefixes(OptArg(args, "allowedMcpTools")), SplitPrefixes(OptArg(args, "allowedPaths")), api));
        }

        return await WithDbAsync(id, requiresTask: true, async scope =>
        {
            var handler = scope.Services.GetRequiredService<CreateSubtaskHandler>();
            var result = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
                scope.TaskId!.Value,
                instructions,
                SplitPrefixes(OptArg(args, "allowedMcpTools")),
                SplitPrefixes(OptArg(args, "allowedPaths")))));
            return DbResult(id, result);
        });
    }

    private static async Task<JsonObject> SubtaskReportResultAsync(JsonNode id, JsonObject? args, BeaconApiClient? api)
    {
        var subtaskId = ParseGuid(args, "subtaskId");
        if (subtaskId is null)
            return ToolError(id, "missing or invalid 'subtaskId' (expected a GUID)");
        var diffRef = OptArg(args, "diffRef");
        if (string.IsNullOrWhiteSpace(diffRef))
            return ToolError(id, "missing 'diffRef'");
        var summary = OptArg(args, "summary");
        if (string.IsNullOrWhiteSpace(summary))
            return ToolError(id, "missing 'summary'");

        if (api is not null)
        {
            if (!TrySessionTask(out var taskId, out var error))
                return ToolError(id, error);
            return await ApiTextAsync(id, await McpApiTools.ReportResultAsync(taskId, subtaskId.Value, diffRef, summary, api));
        }

        return await WithDbAsync(id, requiresTask: true, async scope =>
        {
            var handler = scope.Services.GetRequiredService<ReportSubtaskResultHandler>();
            var result = await handler.HandleAsync(new ReportSubtaskResultCommand(
                new ReportSubtaskResultRequest(scope.TaskId!.Value, subtaskId.Value, diffRef, summary)));
            return DbResult(id, result);
        });
    }

    private static async Task<JsonObject> TaskReviewVerdictAsync(JsonNode id, JsonObject? args, BeaconApiClient? api)
    {
        var kind = ParseVerdict(args);
        if (kind is null)
            return ToolError(id, "missing or invalid 'verdict' (expected approve or reopen_subtask)");
        var note = OptArg(args, "note");
        if (string.IsNullOrWhiteSpace(note))
            return ToolError(id, "missing 'note'");
        var subtaskId = ParseGuid(args, "subtaskId");

        if (api is not null)
        {
            if (!TrySessionTask(out var taskId, out var error))
                return ToolError(id, error);
            return await ApiTextAsync(id, await McpApiTools.ReviewVerdictAsync(taskId, kind.Value, note, subtaskId, api));
        }

        return await WithDbAsync(id, requiresTask: true, async scope =>
        {
            var handler = scope.Services.GetRequiredService<RecordReviewVerdictHandler>();
            var result = await handler.HandleAsync(new RecordReviewVerdictCommand(
                new RecordReviewVerdictRequest(scope.TaskId!.Value, kind.Value, note, subtaskId)));
            return DbResult(id, result);
        });
    }

    private static bool TrySessionTask(out Guid taskId, out string error)
    {
        if (!TryParseScope(out _, out var parsed, out var scopeError))
        {
            taskId = Guid.Empty;
            error = scopeError!;
            return false;
        }

        if (parsed is null)
        {
            taskId = Guid.Empty;
            error = "BEACON_TASK_ID is not set. Set it to the task GUID for this MCP session.";
            return false;
        }

        taskId = parsed.Value;
        error = "";
        return true;
    }

    private static async Task<JsonObject> TaskPipelineStatusAsync(JsonNode id, BeaconApiClient? api)
    {
        if (api is not null)
        {
            if (!TrySessionTask(out var taskId, out var error))
                return ToolError(id, error);
            return await ApiTextAsync(id, await McpApiTools.PipelineStatusAsync(taskId, api));
        }

        return await WithDbAsync(id, requiresTask: true, async scope =>
        {
            var handler = scope.Services.GetRequiredService<GetPipelineHandler>();
            var result = await handler.HandleAsync(new GetPipelineCommand(new GetPipelineRequest(scope.TaskId!.Value)));
            return DbResult(id, result);
        });
    }

    private static IReadOnlyList<string> SplitPrefixes(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];
        return path
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToList();
    }

    private static string FormatTree(TreeResult tree)
    {
        var suffix = tree.Truncated ? ", truncated" : "";
        var lines = new List<string> { $"# Tree ({tree.Entries.Count} entries{suffix})" };
        foreach (var entry in tree.Entries)
        {
            var depth = entry.Path.Count(c => c == '/');
            var name = Path.GetFileName(entry.Path);
            lines.Add(new string(' ', depth * 2) + (entry.IsDirectory ? name + "/" : name));
        }
        return string.Join("\n", lines);
    }

    private static string FormatSearch(SearchResult search)
    {
        if (search.Matches.Count == 0)
            return $"No matches for '{search.Query}'.";

        var suffix = search.Truncated ? " (truncated)" : "";
        return string.Join("\n", search.Matches.Select(m => $"{m.Path}:{m.Line}: {m.Text}")) + suffix;
    }

    private static string FormatFileList(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return "No changed files.";
        return string.Join("\n", files);
    }

    private static string Arg(JsonObject? arguments, string key)
    {
        var value = arguments?[key]?.GetValue<string>();
        if (value is null)
            throw new ArgumentException($"missing {key}");
        return value;
    }

    private static string? OptArg(JsonObject? arguments, string key)
        => arguments?[key]?.GetValue<string>();

    private static int? OptInt(JsonObject? arguments, string key)
    {
        var node = arguments?[key];
        if (node is null)
            return null;
        if (node.GetValueKind() == JsonValueKind.Number)
            return node.GetValue<int>();
        if (node.GetValueKind() == JsonValueKind.String && int.TryParse(node.GetValue<string>(), out var value))
            return value;
        return null;
    }

    private static JsonObject TextResult<T>(JsonNode id, Application.Common.Result<T> result, Func<T, string> format)
        => result.Success
            ? Result(id, Content(format(result.Value!)))
            : ToolError(id, result.Error ?? "error");

    private static JsonObject FileResult(JsonNode id, Application.Common.Result<string> result)
        => result.Success
            ? Result(id, Content(result.Value!))
            : ToolError(id, result.Error ?? "error");

    private static JsonObject BoolResult(JsonNode id, Application.Common.Result result)
        => result.Success
            ? Result(id, Content("ok"))
            : ToolError(id, result.Error ?? "error");

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

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema
    };

    private static JsonObject Props(params (string Name, string Type, bool Required)[] fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in fields)
        {
            properties[field.Name] = new JsonObject { ["type"] = field.Type };
            if (field.Required)
                required.Add(field.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
    }

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
