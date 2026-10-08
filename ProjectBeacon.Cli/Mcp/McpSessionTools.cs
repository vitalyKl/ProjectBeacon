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

public static partial class McpStdioServer
{
    private static JsonObject[] SessionTools() =>
    [
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

    private static async Task<JsonObject> WithDbAsync(JsonNode id, McpEnvironment env, bool requiresTask, Func<McpScope, Task<JsonObject>> invoke)
    {
        if (!TryParseScope(env, out var projectId, out var taskId, out var error))
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
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BeaconDbContext>>().CreateDbContext();
        await using (db)
            await TenantRlsSession.ApplyAsync(db, projectId, null, unscoped: false);
        try
        {
            return await invoke(new McpScope(scope.ServiceProvider, projectId, taskId));
        }
        catch (Exception ex)
        {
            return ToolError(id, ex.Message);
        }
    }

    private static async Task<Guid> OwnerUserIdAsync(McpScope scope)
    {
        var db = scope.Services.GetRequiredService<IDbContextFactory<BeaconDbContext>>().CreateDbContext();
        await using (db)
        {
            return await db.ProjectMembers.IgnoreQueryFilters()
                .Where(m => m.ProjectId == scope.ProjectId && m.Role == MemberRole.Owner)
                .Select(m => m.UserId)
                .FirstOrDefaultAsync();
        }
    }

    private static bool TryParseScope(McpEnvironment env, out Guid projectId, out Guid? taskId, out string? error)
    {
        projectId = Guid.Empty;
        taskId = null;
        var projectRaw = env.Get("BEACON_PROJECT_ID");
        if (string.IsNullOrWhiteSpace(projectRaw) || !Guid.TryParse(projectRaw, out projectId))
        {
            error = "BEACON_PROJECT_ID is not set (or is not a GUID). Set it to the project GUID for this MCP session.";
            return false;
        }

        var taskRaw = env.Get("BEACON_TASK_ID");
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

    private static async Task<JsonObject> ContextCompileAsync(JsonNode id, FileWorkspace workspace, CodeIndex index, BeaconApiClient api, McpEnvironment env)
    {
        var projectId = env.Get("BEACON_PROJECT_ID");
        if (string.IsNullOrWhiteSpace(projectId) || !Guid.TryParse(projectId, out var parsed))
            return ToolError(id, "BEACON_PROJECT_ID is not set.");

        var taskId = env.Get("BEACON_TASK_ID");
        var result = await BriefPatcher.PatchCompileAsync(parsed.ToString("D"), taskId, index, api, CancellationToken.None);
        return result.IsError ? ToolError(id, result.Text) : Result(id, Content(result.Text));
    }

    private static async Task<JsonObject> ModelBindAsync(JsonNode id, JsonObject? args, BeaconApiClient? api, McpEnvironment env)
    {
        var role = ParseRole(args);
        if (role is null)
            return ToolError(id, "missing or invalid 'role' (expected planner, actor or review)");
        var modelId = ParseGuid(args, "modelBackendId");
        if (modelId is null)
            return ToolError(id, "missing or invalid 'modelBackendId' (expected a GUID)");

        if (RejectPartialApi(id) is { } partialBind)
            return partialBind;
        if (api is not null)
        {
            if (!TryParseScope(env, out _, out _, out var error))
                return ToolError(id, error!);
            return await ApiTextAsync(id, await McpApiTools.BindRoleAsync(role.Value, modelId.Value, api));
        }

        return await WithDbAsync(id, env, requiresTask: false, async scope =>
        {
            var handler = scope.Services.GetRequiredService<SetRoleBindingHandler>();
            var result = await handler.HandleAsync(new SetRoleBindingCommand(new SetRoleBindingRequest(role.Value, modelId.Value)));
            return DbResult(id, result);
        });
    }

    private static async Task<JsonObject> ModelStatusAsync(JsonNode id, BeaconApiClient? api, McpEnvironment env)
    {
        if (RejectPartialApi(id) is { } partialStatus)
            return partialStatus;
        if (api is not null)
        {
            if (!TryParseScope(env, out _, out _, out var error))
                return ToolError(id, error!);
            return await ApiTextAsync(id, await McpApiTools.ModelStatusAsync(api));
        }

        return await WithDbAsync(id, env, requiresTask: false, async scope =>
        {
            var ownerId = await OwnerUserIdAsync(scope);
            if (ownerId == Guid.Empty)
                return ToolError(id, "Project has no owner.");
            var registry = await scope.Services
                .GetRequiredService<GetModelRegistryHandler>()
                .HandleAsync(new GetModelRegistryCommand(ownerId));
            if (!registry.Success)
                return ToolError(id, registry.Error ?? "error");

            var proxy = await scope.Services
                .GetRequiredService<GetProxyStatusHandler>()
                .HandleAsync(new GetProxyStatusCommand());

            var status = new JsonObject
            {
                ["userId"] = registry.Value!.UserId,
                ["backends"] = JsonSerializer.SerializeToNode(registry.Value.Backends, JsonDb) ?? new JsonArray(),
                ["bindings"] = JsonSerializer.SerializeToNode(registry.Value.Bindings, JsonDb) ?? new JsonArray(),
                ["templates"] = JsonSerializer.SerializeToNode(registry.Value.Templates, JsonDb) ?? new JsonArray(),
                ["proxy"] = proxy.Success
                    ? JsonSerializer.SerializeToNode(proxy.Value, JsonDb)
                    : new JsonObject { ["available"] = false, ["error"] = proxy.Error ?? "unknown proxy error" }
            };
            return Result(id, Content(status.ToJsonString()));
        });
    }

    private static async Task<JsonObject> TaskCreateSubtaskAsync(JsonNode id, JsonObject? args, BeaconApiClient? api, McpEnvironment env)
    {
        var instructions = OptArg(args, "instructions");
        if (string.IsNullOrWhiteSpace(instructions))
            return ToolError(id, "missing 'instructions'");

        if (RejectPartialApi(id) is { } partialCreate)
            return partialCreate;
        if (api is not null)
        {
            if (!TrySessionTask(env, out var taskId, out var error))
                return ToolError(id, error);
            return await ApiTextAsync(id, await McpApiTools.CreateSubtaskAsync(
                taskId, instructions, SplitPrefixes(OptArg(args, "allowedMcpTools")), SplitPrefixes(OptArg(args, "allowedPaths")), api));
        }

        return await WithDbAsync(id, env, requiresTask: true, async scope =>
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

    private static async Task<JsonObject> SubtaskReportResultAsync(JsonNode id, JsonObject? args, BeaconApiClient? api, McpEnvironment env)
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

        if (RejectPartialApi(id) is { } partialReport)
            return partialReport;
        if (api is not null)
        {
            if (!TrySessionTask(env, out var taskId, out var error))
                return ToolError(id, error);
            return await ApiTextAsync(id, await McpApiTools.ReportResultAsync(taskId, subtaskId.Value, diffRef, summary, api));
        }

        return await WithDbAsync(id, env, requiresTask: true, async scope =>
        {
            var handler = scope.Services.GetRequiredService<ReportSubtaskResultHandler>();
            var result = await handler.HandleAsync(new ReportSubtaskResultCommand(
                new ReportSubtaskResultRequest(scope.TaskId!.Value, subtaskId.Value, diffRef, summary)));
            return DbResult(id, result);
        });
    }

    private static async Task<JsonObject> TaskReviewVerdictAsync(JsonNode id, JsonObject? args, BeaconApiClient? api, McpEnvironment env)
    {
        var kind = ParseVerdict(args);
        if (kind is null)
            return ToolError(id, "missing or invalid 'verdict' (expected approve or reopen_subtask)");
        var note = OptArg(args, "note");
        if (string.IsNullOrWhiteSpace(note))
            return ToolError(id, "missing 'note'");
        var subtaskId = ParseGuid(args, "subtaskId");

        if (RejectPartialApi(id) is { } partialVerdict)
            return partialVerdict;
        if (api is not null)
        {
            if (!TrySessionTask(env, out var taskId, out var error))
                return ToolError(id, error);
            return await ApiTextAsync(id, await McpApiTools.ReviewVerdictAsync(taskId, kind.Value, note, subtaskId, api));
        }

        return await WithDbAsync(id, env, requiresTask: true, async scope =>
        {
            var handler = scope.Services.GetRequiredService<RecordReviewVerdictHandler>();
            var result = await handler.HandleAsync(new RecordReviewVerdictCommand(
                new RecordReviewVerdictRequest(scope.TaskId!.Value, kind.Value, note, subtaskId)));
            return DbResult(id, result);
        });
    }

    private static bool TrySessionTask(McpEnvironment env, out Guid taskId, out string error)
    {
        if (!TryParseScope(env, out _, out var parsed, out var scopeError))
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

    private static JsonObject? RejectPartialApi(JsonNode id) =>
        _apiMisconfigured ? ToolError(id, "BEACON_API_URL and BEACON_API_TOKEN must both be set, or both be unset.") : null;

    private static async Task<JsonObject> TaskPipelineStatusAsync(JsonNode id, BeaconApiClient? api, McpEnvironment env)
    {
        if (RejectPartialApi(id) is { } partialPipeline)
            return partialPipeline;
        if (api is not null)
        {
            if (!TrySessionTask(env, out var taskId, out var error))
                return ToolError(id, error);
            return await ApiTextAsync(id, await McpApiTools.PipelineStatusAsync(taskId, api));
        }

        return await WithDbAsync(id, env, requiresTask: true, async scope =>
        {
            var handler = scope.Services.GetRequiredService<GetPipelineHandler>();
            var result = await handler.HandleAsync(new GetPipelineCommand(new GetPipelineRequest(scope.TaskId!.Value)));
            return DbResult(id, result);
        });
    }
}