namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Common;
using Domain.Entities.Tasks;
using Domain.Enums;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> PipelineTools() =>
    [
        new(McpToolNames.PipelineStart, "Start the task pipeline. Uses BEACON_TASK_ID when taskId is omitted.",
            () => Props(("taskId", "string", false)), PipelineStart),
        new(McpToolNames.PipelineStartActor, "Start an actor session for a subtask.",
            () => Props(("subtaskId", "string", true), ("taskId", "string", false)), PipelineActor),
        new(McpToolNames.PipelineLaunchSession, "Launch a pipeline session on the workstation.",
            () => Props(("sessionId", "string", true)), PipelineLaunch),
        new(McpToolNames.PipelineFailSubtask, "Fail an in-progress subtask. In Actor sessions, subtaskId defaults to the assigned subtask; a supplied ID must match.",
            () => Props(("subtaskId", "string", false), ("reason", "string", true), ("taskId", "string", false)), PipelineFail),
        new(McpToolNames.PipelineStartReview, "Start the review stage of the pipeline.",
            () => Props(("taskId", "string", false)), PipelineReview),
        new(McpToolNames.PipelineApprove, "Approve the pipeline and close the task.",
            () => Props(("note", "string", false), ("taskId", "string", false)), PipelineApprove),
        new(McpToolNames.PipelineForceClose, "Force-close a pipeline. The API token needs the Admin capability.",
            () => Props(("reason", "string", false), ("taskId", "string", false)), PipelineForceClose),
        new(McpToolNames.PipelineLandRequest, "Request a verified preview for the approved task branch. This checks the pinned review proof and does not merge the branch.",
            () => Props(("rationale", "string", true), ("taskId", "string", false)), PipelineLandRequest),
        new(McpToolNames.PipelineLandRevert, "Request a guarded revert of a landed task attempt. The target must still point exactly at the landing commit.",
            () => Props(("landingAttemptId", "string", true), ("taskId", "string", false)), PipelineLandRevert),
    ];

    private static async Task<McpToolText> PipelineStart(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/start", new JsonObject { ["taskId"] = taskId.ToString("D") }, ct);
    }

    private static async Task<McpToolText> PipelineActor(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "subtaskId", out var subtaskId))
            return new McpToolText(true, "missing or invalid 'subtaskId'");
        var body = new JsonObject { ["taskId"] = taskId.ToString("D"), ["subtaskId"] = subtaskId.ToString("D") };
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/subtasks/{subtaskId:D}/session", body, ct);
    }

    private static async Task<McpToolText> PipelineLaunch(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var taskError))
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
        if (!TryPipelineTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "subtaskId", out var subtaskId))
            return new McpToolText(true, "missing or invalid 'subtaskId'");
        var reason = Text(args, "reason");
        if (string.IsNullOrWhiteSpace(reason))
            return new McpToolText(true, "missing 'reason'");
        if (reason.Length > Subtask.MaxSummaryLength)
            return new McpToolText(true, $"Failure reason exceeds the {Subtask.MaxSummaryLength}-character limit. Shorten it and retry.");
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
        if (!TryPipelineTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/review/start", new JsonObject { ["taskId"] = taskId.ToString("D") }, ct);
    }

    private static async Task<McpToolText> PipelineApprove(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject { ["taskId"] = taskId.ToString("D") };
        Put(body, "note", Text(args, "note"));
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/approve", body, ct);
    }

    private static async Task<McpToolText> PipelineLandRequest(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        var rationale = Text(args, "rationale");
        if (string.IsNullOrWhiteSpace(rationale))
            return new McpToolText(true, "missing 'rationale'");
        if (rationale.Length > 1000)
            return new McpToolText(true, "rationale exceeds the 1000-character limit.");
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["rationale"] = rationale
        };
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/land", body, ct);
    }

    private static async Task<McpToolText> PipelineLandRevert(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "landingAttemptId", out var attemptId))
            return new McpToolText(true, "missing or invalid 'landingAttemptId'");
        var body = new JsonObject { ["taskId"] = taskId.ToString("D") };
        return await SendAsync(api, HttpMethod.Post,
            $"v1/tasks/{taskId:D}/pipeline/land/{attemptId:D}/revert", body, ct);
    }

    private static async Task<McpToolText> PipelineForceClose(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryPipelineTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject { ["taskId"] = taskId.ToString("D") };
        Put(body, "reason", Text(args, "reason"));
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/force-close", body, ct);
    }

}