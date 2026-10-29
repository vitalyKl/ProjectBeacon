namespace ProjectBeacon.Cli.Mcp;

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
            return Result(id, new JsonObject { ["tools"] = new JsonArray() });

        Guid? explicitSubtaskId = Guid.TryParse(env.Get("BEACON_SUBTASK_ID"), out var subtaskId)
            ? subtaskId
            : null;
        var filtered = SubtaskScopeGuard.FilterTools(tools, allowlists, explicitSubtaskId);
        return Result(id, new JsonObject { ["tools"] = filtered });
    }
}
