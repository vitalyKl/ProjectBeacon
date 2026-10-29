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
        return Result(id, new JsonObject { ["tools"] = filtered });
    }

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
