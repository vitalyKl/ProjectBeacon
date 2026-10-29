namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;

public static partial class McpStdioServer
{
    private static async Task<JsonObject> ListToolsAsync(JsonNode id, BeaconApiClient? api, McpEnvironment env)
    {
        var tools = Tools();
        if (api is null || !Guid.TryParse(env.Get("BEACON_TASK_ID"), out var taskId) || taskId == Guid.Empty)
            return Result(id, new JsonObject { ["tools"] = tools });

        var (ok, _, body) = await api.SendAsync(
            HttpMethod.Get, $"v1/tasks/{taskId:D}/pipeline", null, CancellationToken.None);
        if (!ok)
            return Result(id, new JsonObject { ["tools"] = new JsonArray() });

        var allowlists = SubtaskScopeGuard.ParsePipeline(body);
        if (allowlists.Count == 0)
        {
            // A newly started Planner has no subtasks yet. Expose the normal tool catalog
            // only when the API explicitly confirms an empty list; malformed or incomplete
            // pipeline responses remain fail-closed.
            var toolsForEmptyPipeline = HasExplicitlyEmptySubtaskList(body) ? tools : new JsonArray();
            return Result(id, new JsonObject { ["tools"] = toolsForEmptyPipeline });
        }

        Guid? explicitSubtaskId = Guid.TryParse(env.Get("BEACON_SUBTASK_ID"), out var subtaskId)
            ? subtaskId
            : null;
        var filtered = SubtaskScopeGuard.FilterTools(tools, allowlists, explicitSubtaskId);
        var supervisorTools = PipelineSupervisorTools(env);
        foreach (var tool in tools)
        {
            var name = tool?["name"]?.GetValue<string>();
            if (name is not null
                && supervisorTools.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !filtered.Any(existing => string.Equals(
                    existing?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)))
                filtered.Add(tool!.DeepClone());
        }
        return Result(id, new JsonObject { ["tools"] = filtered });
    }

    private static readonly string[] PlannerSupervisorTools =
    [
        "context_compile", "task_create_subtask", "pipeline_start_actor",
        "pipeline_launch_session", "task_pipeline_status", "pipeline_start_review"
    ];

    private static readonly string[] ReviewerSupervisorTools =
    [
        "task_pipeline_status", "task_review_verdict", "pipeline_approve"
    ];

    private static IReadOnlyList<string> PipelineSupervisorTools(McpEnvironment env)
    {
        var role = env.Get("BEACON_PIPELINE_ROLE");
        if (string.Equals(role, "Planner", StringComparison.OrdinalIgnoreCase))
            return PlannerSupervisorTools;
        if (string.Equals(role, "Review", StringComparison.OrdinalIgnoreCase))
            return ReviewerSupervisorTools;
        return [];
    }

    private static bool IsPipelineSupervisorTool(string tool, McpEnvironment env)
        => PipelineSupervisorTools(env).Contains(tool, StringComparer.OrdinalIgnoreCase);

    private static bool HasExplicitlyEmptySubtaskList(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("subtasks", out var subtasks)
                && subtasks.ValueKind == JsonValueKind.Array
                && subtasks.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
