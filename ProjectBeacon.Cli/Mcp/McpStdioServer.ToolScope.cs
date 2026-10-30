namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;

public static partial class McpStdioServer
{
    private static async Task<JsonObject> ListToolsAsync(JsonNode id, BeaconApiClient? api, McpEnvironment env)
    {
        var tools = Tools();
        var role = env.Get("BEACON_PIPELINE_ROLE");
        if (string.Equals(role, "Eval", StringComparison.OrdinalIgnoreCase))
            return Result(id, new JsonObject { ["tools"] = FilterToolsByName(tools, EvalAllowedTools) });

        if (api is null || !Guid.TryParse(env.Get("BEACON_TASK_ID"), out var taskId) || taskId == Guid.Empty)
        {
            if (IsPipelineRole(role))
                return Result(id, new JsonObject { ["tools"] = new JsonArray() });
            return Result(id, new JsonObject { ["tools"] = tools });
        }

        var (ok, _, body) = await api.SendAsync(
            HttpMethod.Get, $"v1/tasks/{taskId:D}/pipeline", null, CancellationToken.None);
        if (!ok || !HasSubtasksArray(body))
            return Result(id, new JsonObject { ["tools"] = new JsonArray() });

        var allowlists = SubtaskScopeGuard.ParsePipeline(body);
        if (string.Equals(role, "Planner", StringComparison.OrdinalIgnoreCase))
            return Result(id, new JsonObject { ["tools"] = FilterToolsByName(tools, PlannerTools) });
        if (string.Equals(role, "Review", StringComparison.OrdinalIgnoreCase))
            return Result(id, new JsonObject { ["tools"] = FilterToolsByName(tools, ReviewerTools) });

        if (allowlists.Count == 0)
        {
            // Planner and Reviewer were handled by their explicit role allowlists above.
            // With no valid subtask scope and no supervisor role, fail closed.
            return Result(id, new JsonObject { ["tools"] = new JsonArray() });
        }

        Guid? explicitSubtaskId = Guid.TryParse(env.Get("BEACON_SUBTASK_ID"), out var subtaskId)
            ? subtaskId
            : null;
        if (string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase))
        {
            if (explicitSubtaskId is not { } actorSubtaskId)
                return Result(id, new JsonObject { ["tools"] = new JsonArray() });

            var actorSubtask = allowlists.FirstOrDefault(s => s.Id == actorSubtaskId);
            if (actorSubtask.Id == Guid.Empty
                || !string.Equals(actorSubtask.Status, "InProgress", StringComparison.OrdinalIgnoreCase))
                return Result(id, new JsonObject { ["tools"] = new JsonArray() });
        }

        var filtered = SubtaskScopeGuard.FilterTools(tools, allowlists, explicitSubtaskId);
        if (string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase))
            filtered = FilterActorTools(filtered, allowlists, explicitSubtaskId);

        // Actors must always be able to report success or failure for their own subtask,
        // even when the caller omitted these mandatory lifecycle tools from the allowlist.
        if (string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var tool in tools)
            {
                var name = tool?["name"]?.GetValue<string>();
                if (name is not null
                    && ActorTerminalTools.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !filtered.Any(existing => string.Equals(
                        existing?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)))
                    filtered.Add(tool!.DeepClone());
            }
        }

        return Result(id, new JsonObject { ["tools"] = filtered });
    }

    private static readonly string[] ReadOnlyWorkspaceTools =
    [
        "read_file", "get_tree", "search_code", "get_changed_scope",
        "get_signatures", "get_callers", "hash_range"
    ];

    private static readonly string[] PlannerSupervisorTools =
    [
        "context_compile", "task_create_subtask", "pipeline_start_actor",
        "pipeline_launch_session", "task_pipeline_status", "pipeline_start_review"
    ];

    private static readonly string[] ReviewerSupervisorTools =
    [
        "task_pipeline_status", "task_review_verdict", "pipeline_approve"
    ];

    private static readonly string[] PlannerTools =
    [
        ..ReadOnlyWorkspaceTools,
        "get_project",
        "list_context_nodes", "get_context_node", "export_agents_md",
        "list_constraints", "list_decisions", "list_milestones", "list_labels", "match_label",
        ..PlannerSupervisorTools
    ];

    private static readonly string[] ReviewerTools =
    [
        ..ReadOnlyWorkspaceTools,
        "get_project",
        "list_context_nodes", "get_context_node",
        ..ReviewerSupervisorTools
    ];

    private static readonly string[] ActorTerminalTools =
    [
        "subtask_report_result", "pipeline_fail_subtask"
    ];

    private static readonly string[] EvalAllowedTools =
    [
        ..ReadOnlyWorkspaceTools,
        "write_file", "apply_patch",
        "get_project", "list_tasks", "get_task", "list_task_steps",
        "list_context_nodes", "get_context_node", "export_agents_md",
        "list_constraints", "list_decisions", "list_milestones", "get_milestone",
        "list_labels", "match_label", "list_reports", "get_report"
    ];

    private static readonly string[] ActorForbiddenTools =
    [
        "task_create_subtask", "task_review_verdict", "task_pipeline_status",
        "pipeline_start", "pipeline_start_actor", "pipeline_launch_session",
        "pipeline_start_review", "pipeline_approve", "pipeline_force_close",
        "model_bind", "model_status", "model_upsert", "model_delete", "model_unbind",
        "proxy_reload", "proxy_unload",
        "create_task", "update_task", "set_task_status", "set_task_substage",
        "claim_task", "add_task_comment", "set_task_dependencies", "add_review_notes",
        "list_task_steps", "add_task_step", "toggle_task_step", "delete_task_step", "finish_work",
        "upsert_context_node", "delete_context_node",
        "create_constraint", "activate_constraint", "reject_constraint",
        "record_decision", "accept_decision", "deprecate_decision", "supersede_decision",
        "create_milestone", "update_milestone", "delete_milestone", "close_milestone", "reopen_milestone",
        "add_label_path", "generate_report"
    ];

    private static JsonArray FilterToolsByName(JsonArray tools, IReadOnlyList<string> names)
    {
        var allowed = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            var name = tool?["name"]?.GetValue<string>();
            if (name is not null && allowed.Contains(name))
                result.Add(tool!.DeepClone());
        }
        return result;
    }

    private static JsonArray FilterActorTools(
        JsonArray tools,
        IReadOnlyList<SubtaskScopeGuard.Allowlist> allowlists,
        Guid? explicitSubtaskId)
    {
        var result = new JsonArray();
        if (explicitSubtaskId is not { } id)
            return result;

        var active = allowlists.FirstOrDefault(s => s.Id == id);
        if (active.Id == Guid.Empty)
            return result;

        foreach (var tool in tools)
        {
            var name = tool?["name"]?.GetValue<string>();
            if (name is null || !IsActorToolAllowed(name))
                continue;

            // Empty AllowedMcpTools has historically meant "unrestricted" for generic MCP
            // clients. Pipeline Actors have a stricter contract: only explicitly granted
            // workspace tools are exposed, while lifecycle tools are added below.
            if (!ActorTerminalTools.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !IsGrantedToSubtask(active.Tools, name))
                continue;

            result.Add(tool!.DeepClone());
        }
        return result;
    }

    private static bool IsGrantedToSubtask(IReadOnlyList<string> grants, string tool)
        => grants.Any(entry =>
            string.Equals(entry, tool, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry, "beacon_" + tool, StringComparison.OrdinalIgnoreCase));

    private static string? RejectActorToolGrant(
        string tool,
        IReadOnlyList<SubtaskScopeGuard.Allowlist> allowlists,
        Guid? explicitSubtaskId)
    {
        if (explicitSubtaskId is not { } id)
            return "Actor pipeline sessions require an explicit subtask scope.";

        var active = allowlists.FirstOrDefault(s => s.Id == id);
        if (active.Id == Guid.Empty)
            return "subtask not found";

        return IsGrantedToSubtask(active.Tools, tool)
            ? null
            : $"tool '{tool}' is not allowed for this subtask";
    }

    private static bool IsActorToolAllowed(string tool)
    {
        if (ActorTerminalTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
            return true;
        if (ActorForbiddenTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
            return false;
        // Actors can use workspace tools from their subtask allowlist, but only the
        // mandatory result/failure calls may invoke the general Beacon control plane.
        return !McpApiTools.IsApiTool(tool);
    }

    private static bool IsPipelineRole(string? role)
        => string.Equals(role, "Planner", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Review", StringComparison.OrdinalIgnoreCase);

    private static string? ValidatePipelineRoleTool(string tool, McpEnvironment env)
    {
        var role = env.Get("BEACON_PIPELINE_ROLE");
        if (string.Equals(role, "Planner", StringComparison.OrdinalIgnoreCase))
            return PlannerTools.Contains(tool, StringComparer.OrdinalIgnoreCase)
                ? null
                : $"Tool '{tool}' is not allowed for the Planner pipeline role.";
        if (string.Equals(role, "Review", StringComparison.OrdinalIgnoreCase))
            return ReviewerTools.Contains(tool, StringComparer.OrdinalIgnoreCase)
                ? null
                : $"Tool '{tool}' is not allowed for the Reviewer pipeline role.";
        if (string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase))
            return IsActorToolAllowed(tool)
                ? null
                : $"Tool '{tool}' is not allowed for the Actor pipeline role.";
        if (string.Equals(role, "Eval", StringComparison.OrdinalIgnoreCase))
            return EvalAllowedTools.Contains(tool, StringComparer.OrdinalIgnoreCase)
                ? null
                : $"Tool '{tool}' is not allowed for the Eval runtime scope.";
        return null;
    }

    private static bool IsActorTerminalTool(string tool)
        => ActorTerminalTools.Contains(tool, StringComparer.OrdinalIgnoreCase);

    private static bool HasSubtasksArray(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("subtasks", out var subtasks)
                && subtasks.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

}
