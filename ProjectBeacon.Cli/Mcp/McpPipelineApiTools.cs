namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Enums;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> PipelineTools() =>
    [
        new("pipeline_start", "Start the task pipeline. Uses BEACON_TASK_ID when taskId is omitted.",
            () => Props(("taskId", "string", false)), PipelineStart),
        new("pipeline_start_actor", "Start an actor session for a subtask.",
            () => Props(("subtaskId", "string", true), ("taskId", "string", false)), PipelineActor),
        new("pipeline_launch_session", "Launch a pipeline session on the workstation.",
            () => Props(("sessionId", "string", true)), PipelineLaunch),
        new("pipeline_fail_subtask", "Fail an in-progress subtask.",
            () => Props(("subtaskId", "string", true), ("reason", "string", true), ("taskId", "string", false)), PipelineFail),
        new("pipeline_start_review", "Start the review stage of the pipeline.",
            () => Props(("taskId", "string", false)), PipelineReview),
        new("pipeline_approve", "Approve the pipeline and close the task.",
            () => Props(("note", "string", false), ("taskId", "string", false)), PipelineApprove),
        new("pipeline_force_close", "Force-close a pipeline. The API token needs the Admin capability.",
            () => Props(("reason", "string", false), ("taskId", "string", false)), PipelineForceClose),
    ];

    private static async Task<McpToolText> PipelineStart(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/start", new JsonObject { ["taskId"] = taskId.ToString("D") }, ct);
    }

    private static async Task<McpToolText> PipelineActor(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "subtaskId", out var subtaskId))
            return new McpToolText(true, "missing or invalid 'subtaskId'");
        var body = new JsonObject { ["taskId"] = taskId.ToString("D"), ["subtaskId"] = subtaskId.ToString("D") };
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/subtasks/{subtaskId:D}/session", body, ct);
    }

    private static async Task<McpToolText> PipelineLaunch(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryTask(env, args, out var taskId, out var taskError))
            return new McpToolText(true, taskError);
        if (!TryGuid(args, "sessionId", out var sessionId))
            return new McpToolText(true, "missing or invalid 'sessionId'");

        // The launch endpoint is session-id scoped, not task-id scoped. Verify membership
        // against the current task before sending the command so a Planner cannot launch
        // an unrelated session in the same project with its short-lived project token.
        var pipeline = await PipelineStatusAsync(taskId, api, ct);
        if (pipeline.IsError)
            return new McpToolText(true, pipeline.Text);
        try
        {
            using var doc = JsonDocument.Parse(pipeline.Text);
            if (!doc.RootElement.TryGetProperty("sessions", out var sessions)
                || sessions.ValueKind != JsonValueKind.Array
                || !sessions.EnumerateArray().Any(session =>
                    session.ValueKind == JsonValueKind.Object
                    && session.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && Guid.TryParse(id.GetString(), out var parsed)
                    && parsed == sessionId))
                return new McpToolText(true, "sessionId does not belong to the current pipeline task.");
        }
        catch (JsonException)
        {
            return new McpToolText(true, "Could not verify that sessionId belongs to the current pipeline task.");
        }

        return await SendAsync(api, HttpMethod.Post, $"v1/sessions/{sessionId:D}/launch", new JsonObject { ["sessionId"] = sessionId.ToString("D") }, ct);
    }

    private static async Task<McpToolText> PipelineFail(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "subtaskId", out var subtaskId))
            return new McpToolText(true, "missing or invalid 'subtaskId'");
        var reason = Text(args, "reason");
        if (string.IsNullOrWhiteSpace(reason))
            return new McpToolText(true, "missing 'reason'");
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["subtaskId"] = subtaskId.ToString("D"),
            ["reason"] = reason
        };
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/subtasks/{subtaskId:D}/fail", body, ct);
    }

    private static async Task<McpToolText> PipelineReview(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/review/start", new JsonObject { ["taskId"] = taskId.ToString("D") }, ct);
    }

    private static async Task<McpToolText> PipelineApprove(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject { ["taskId"] = taskId.ToString("D") };
        Put(body, "note", Text(args, "note"));
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/approve", body, ct);
    }

    private static async Task<McpToolText> PipelineForceClose(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject { ["taskId"] = taskId.ToString("D") };
        Put(body, "reason", Text(args, "reason"));
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/force-close", body, ct);
    }

}