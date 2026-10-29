namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json.Nodes;
using Domain.Enums;

public static partial class McpStdioServer
{
    private static JsonObject[] SessionTools() =>
    [
        Tool("model_bind", "Bind a pipeline role (planner, actor, review) to a local model backend. Requires Beacon API credentials and BEACON_PROJECT_ID.",
            Props(("role", "string", true), ("modelBackendId", "string", true))),
        Tool("model_status", "Show local model backends, role bindings and llama-swap proxy status. Requires Beacon API credentials and BEACON_PROJECT_ID.",
            Props()),
        Tool("task_create_subtask", "Create a subtask on the session task. Requires Beacon API credentials, BEACON_PROJECT_ID and BEACON_TASK_ID.",
            Props(("instructions", "string", true), ("allowedMcpTools", "string", false), ("allowedPaths", "string", false))),
        Tool("subtask_report_result", "Report the result of an in-progress subtask. Requires Beacon API credentials, BEACON_PROJECT_ID and BEACON_TASK_ID.",
            Props(("subtaskId", "string", true), ("diffRef", "string", true), ("summary", "string", true))),
        Tool("task_review_verdict", "Record a review verdict (approve, reopen_subtask) on the session task. Requires Beacon API credentials, BEACON_PROJECT_ID and BEACON_TASK_ID.",
            Props(("verdict", "string", true), ("note", "string", true), ("subtaskId", "string", false))),
        Tool("task_pipeline_status", "Show the pipeline state (subtasks, sessions, verdicts) of the session task. Requires Beacon API credentials, BEACON_PROJECT_ID and BEACON_TASK_ID.",
            Props())
    ];

    private static JsonObject? RequireApi(JsonNode id, BeaconApiClient? api, McpEnvironment env)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(env.Get("BEACON_API_URL"));
        var hasToken = !string.IsNullOrWhiteSpace(env.Get("BEACON_API_TOKEN"));
        if (hasUrl != hasToken)
            return ToolError(id, "BEACON_API_URL and BEACON_API_TOKEN must both be set, or both be unset.");
        if (api is not null)
            return null;
        if (hasUrl)
            return ToolError(id, "BEACON_API_URL must be a valid absolute HTTP(S) URL.");
        return ToolError(id, "BEACON_API_URL and BEACON_API_TOKEN are required for ProjectBeacon control-plane tools. File tools remain available without them.");
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
        return Guid.TryParse(raw, out var value) ? value : null;
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

    private static Task<JsonObject> ApiTextAsync(JsonNode id, McpToolText text) =>
        Task.FromResult(text.IsError ? ToolError(id, text.Text) : Result(id, Content(text.Text)));

    private static async Task<JsonObject> ContextCompileAsync(JsonNode id, FileWorkspace workspace, CodeIndex index, BeaconApiClient api, McpEnvironment env)
    {
        if (!TryParseScope(env, out var projectId, out var taskId, out var error))
            return ToolError(id, error!);

        var result = await BriefPatcher.PatchCompileAsync(projectId.ToString("D"), taskId?.ToString("D"), index, api, CancellationToken.None);
        return result.IsError ? ToolError(id, result.Text) : Result(id, Content(result.Text));
    }

    private static async Task<JsonObject> ModelBindAsync(JsonNode id, JsonObject? args, BeaconApiClient? api, McpEnvironment env)
    {
        if (RequireApi(id, api, env) is { } apiError)
            return apiError;
        var role = ParseRole(args);
        if (role is null)
            return ToolError(id, "missing or invalid 'role' (expected planner, actor or review)");
        var modelId = ParseGuid(args, "modelBackendId");
        if (modelId is null)
            return ToolError(id, "missing or invalid 'modelBackendId' (expected a GUID)");
        if (!TryParseScope(env, out _, out _, out var error))
            return ToolError(id, error!);
        return await ApiTextAsync(id, await McpApiTools.BindRoleAsync(role.Value, modelId.Value, api!));
    }

    private static async Task<JsonObject> ModelStatusAsync(JsonNode id, BeaconApiClient? api, McpEnvironment env)
    {
        if (RequireApi(id, api, env) is { } apiError)
            return apiError;
        if (!TryParseScope(env, out _, out _, out var error))
            return ToolError(id, error!);
        return await ApiTextAsync(id, await McpApiTools.ModelStatusAsync(api!));
    }

    private static async Task<JsonObject> TaskCreateSubtaskAsync(JsonNode id, JsonObject? args, BeaconApiClient? api, McpEnvironment env)
    {
        if (RequireApi(id, api, env) is { } apiError)
            return apiError;
        var instructions = OptArg(args, "instructions");
        if (string.IsNullOrWhiteSpace(instructions))
            return ToolError(id, "missing 'instructions'");
        if (!TrySessionTask(env, out var taskId, out var error))
            return ToolError(id, error);
        return await ApiTextAsync(id, await McpApiTools.CreateSubtaskAsync(
            taskId, instructions, SplitPrefixes(OptArg(args, "allowedMcpTools")), SplitPrefixes(OptArg(args, "allowedPaths")), api!));
    }

    private static async Task<JsonObject> SubtaskReportResultAsync(JsonNode id, JsonObject? args, BeaconApiClient? api, McpEnvironment env)
    {
        if (RequireApi(id, api, env) is { } apiError)
            return apiError;
        var subtaskId = ParseGuid(args, "subtaskId");
        if (subtaskId is null)
            return ToolError(id, "missing or invalid 'subtaskId' (expected a GUID)");
        var diffRef = OptArg(args, "diffRef");
        if (string.IsNullOrWhiteSpace(diffRef))
            return ToolError(id, "missing 'diffRef'");
        var summary = OptArg(args, "summary");
        if (string.IsNullOrWhiteSpace(summary))
            return ToolError(id, "missing 'summary'");
        if (!TrySessionTask(env, out var taskId, out var error))
            return ToolError(id, error);
        return await ApiTextAsync(id, await McpApiTools.ReportResultAsync(taskId, subtaskId.Value, diffRef, summary, api!));
    }

    private static async Task<JsonObject> TaskReviewVerdictAsync(JsonNode id, JsonObject? args, BeaconApiClient? api, McpEnvironment env)
    {
        if (RequireApi(id, api, env) is { } apiError)
            return apiError;
        var kind = ParseVerdict(args);
        if (kind is null)
            return ToolError(id, "missing or invalid 'verdict' (expected approve or reopen_subtask)");
        var note = OptArg(args, "note");
        if (string.IsNullOrWhiteSpace(note))
            return ToolError(id, "missing 'note'");
        var subtaskId = ParseGuid(args, "subtaskId");
        if (!TrySessionTask(env, out var taskId, out var error))
            return ToolError(id, error);
        return await ApiTextAsync(id, await McpApiTools.ReviewVerdictAsync(taskId, kind.Value, note, subtaskId, api!));
    }

    private static async Task<JsonObject> TaskPipelineStatusAsync(JsonNode id, BeaconApiClient? api, McpEnvironment env)
    {
        if (RequireApi(id, api, env) is { } apiError)
            return apiError;
        if (!TrySessionTask(env, out var taskId, out var error))
            return ToolError(id, error);
        return await ApiTextAsync(id, await McpApiTools.PipelineStatusAsync(taskId, api!));
    }
}
