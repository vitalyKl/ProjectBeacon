namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Enums;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> TaskTools() =>
    [
        new("get_project", "Get the current project from the Beacon API.",
            () => Props(("projectId", "string", false)), ProjectGet),
        new("list_tasks", "List tasks on the board and backlog. Optional status: Todo, InProgress, Done.",
            () => Props(("projectId", "string", false), ("status", "string", false)), TasksList),
        new("get_task", "Get one task, including comments and dependencies.",
            () => Props(("taskId", "string", false), ("projectId", "string", false)), TaskGet),
        new("create_task", "Create a task. priority: Low, Medium, High, Critical. type: Feature, Bug, Improvement, Task.",
            () => Props(("title", "string", true), ("description", "string", false), ("priority", "string", false),
                ("type", "string", false), ("labelId", "string", false), ("milestoneId", "string", false),
                ("path", "string", false), ("projectId", "string", false)), TaskCreate),
        new("update_task", "Update a task title, description, priority, type, label, or milestone.",
            () => Props(("taskId", "string", true), ("title", "string", false), ("description", "string", false),
                ("priority", "string", false), ("type", "string", false), ("labelId", "string", false),
                ("milestoneId", "string", false), ("projectId", "string", false)), TaskUpdate),
        new("set_task_status", "Move a task. status: Todo, InProgress, Done. Done still requires review notes.",
            () => Props(("taskId", "string", true), ("status", "string", true), ("projectId", "string", false)), TaskStatus),
        new("set_task_substage", "Set the in-progress sub-stage shown on the board.",
            () => Props(("taskId", "string", true), ("subStage", "string", true)), TaskSubStage),
        new("claim_task", "Atomically claim a Todo task (Todo to InProgress). Uses BEACON_TASK_ID when taskId is omitted.",
            () => Props(("taskId", "string", false), ("projectId", "string", false)), TaskClaim),
        new("add_task_comment", "Add a comment on a task.",
            () => Props(("taskId", "string", true), ("content", "string", true)), TaskComment),
        new("set_task_dependencies", "Replace the tasks this task depends on.",
            () => Props(("taskId", "string", true), ("dependentTaskIds", "array", true)), TaskDependencies),
        new("add_review_notes", "Save review notes required before a task can move to Done.",
            () => Props(("taskId", "string", true), ("reviewNotes", "string", true)), TaskReviewNotes),
        new("list_task_steps", "List checklist steps on a task.",
            () => Props(("taskId", "string", true)), StepsList),
        new("add_task_step", "Add a checklist step.",
            () => Props(("taskId", "string", true), ("title", "string", true)), StepAdd),
        new("toggle_task_step", "Mark a checklist step done or not done.",
            () => Props(("stepId", "string", true), ("done", "boolean", true)), StepToggle),
        new("delete_task_step", "Delete a checklist step.",
            () => Props(("stepId", "string", true)), StepDelete),
        new("finish_work", "Finish work on a task. result: done, failed, skipped, or partial. done requires a completed review run with a target and check artifact (reviewRunId). reviewerRun alone is not proof.",
            FinishSchema, Finish),
    ];

    private static async Task<McpToolText> ProjectGet(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}", null, ct);
    }

    private static async Task<McpToolText> TasksList(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var status = Text(args, "status");
        var query = string.IsNullOrWhiteSpace(status) ? "" : "?status=" + Uri.EscapeDataString(status);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/tasks{query}", null, ct);
    }

    private static async Task<McpToolText> TaskGet(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        if (TryProject(env, args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/tasks/{taskId:D}", null, ct);
        return await SendAsync(api, HttpMethod.Get, $"v1/tasks/{taskId:D}", null, ct);
    }

    private static async Task<McpToolText> TaskCreate(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        var title = Text(args, "title");
        if (string.IsNullOrWhiteSpace(title))
            return new McpToolText(true, "missing 'title'");
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject
        {
            ["title"] = title,
            ["projectId"] = projectId.ToString("D"),
            ["priority"] = Text(args, "priority") ?? "Medium",
            ["type"] = Text(args, "type") ?? "Task"
        };
        Put(body, "description", Text(args, "description"));
        Put(body, "labelId", Text(args, "labelId"));
        Put(body, "milestoneId", Text(args, "milestoneId"));
        Put(body, "path", Text(args, "path"));
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/tasks", body, ct);
    }

    private static async Task<McpToolText> TaskUpdate(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var body = new JsonObject { ["taskId"] = taskId.ToString("D") };
        Put(body, "title", Text(args, "title"));
        Put(body, "description", Text(args, "description"));
        Put(body, "priority", Text(args, "priority"));
        Put(body, "type", Text(args, "type"));
        Put(body, "labelId", Text(args, "labelId"));
        Put(body, "milestoneId", Text(args, "milestoneId"));
        if (!TryProject(env, args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Put, $"v1/tasks/{taskId:D}", body, ct);
        return await SendAsync(api, HttpMethod.Put, $"v1/projects/{projectId:D}/tasks/{taskId:D}", body, ct);
    }

    private static async Task<McpToolText> TaskStatus(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var status = Text(args, "status");
        if (string.IsNullOrWhiteSpace(status))
            return new McpToolText(true, "missing 'status'");
        var body = new JsonObject { ["status"] = status };
        if (!TryProject(env, args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Patch, $"v1/tasks/{taskId:D}/status", body, ct);
        return await SendAsync(api, HttpMethod.Patch, $"v1/projects/{projectId:D}/tasks/{taskId:D}/status", body, ct);
    }

    private static async Task<McpToolText> TaskSubStage(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var subStage = Text(args, "subStage");
        if (string.IsNullOrWhiteSpace(subStage))
            return new McpToolText(true, "missing 'subStage'");
        return await SendAsync(api, HttpMethod.Patch, $"v1/tasks/{taskId:D}/substage", JsonValue.Create(subStage), ct);
    }

    private static async Task<McpToolText> TaskClaim(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryTask(env, args, out var taskId, out var error))
            return new McpToolText(true, error);
        if (!TryProject(env, args, out var projectId, out var projectError))
            return new McpToolText(true, projectError);
        return await SendAsync(api, HttpMethod.Patch, $"v1/projects/{projectId:D}/tasks/{taskId:D}/claim", null, ct);
    }

    private static async Task<McpToolText> TaskComment(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var content = Text(args, "content");
        if (string.IsNullOrWhiteSpace(content))
            return new McpToolText(true, "missing 'content'");
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["content"] = content
        };
        if (!TryProject(env, args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/comments", body, ct);
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/tasks/{taskId:D}/comments", body, ct);
    }

    private static async Task<McpToolText> TaskDependencies(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        if (!TryGuidList(args, "dependentTaskIds", out var ids, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["dependentTaskIds"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id.ToString("D"))!).ToArray())
        };
        if (!TryProject(env, args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Put, $"v1/tasks/{taskId:D}/dependencies", body, ct);
        return await SendAsync(api, HttpMethod.Put, $"v1/projects/{projectId:D}/tasks/{taskId:D}/dependencies", body, ct);
    }

    private static async Task<McpToolText> TaskReviewNotes(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var notes = Text(args, "reviewNotes");
        if (string.IsNullOrWhiteSpace(notes))
            return new McpToolText(true, "missing 'reviewNotes'");
        if (!TryProject(env, args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Patch, $"v1/tasks/{taskId:D}/review-notes", JsonValue.Create(notes), ct);
        return await SendAsync(api, HttpMethod.Patch, $"v1/projects/{projectId:D}/tasks/{taskId:D}/review-notes", JsonValue.Create(notes), ct);
    }

    private static async Task<McpToolText> StepsList(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        return await SendAsync(api, HttpMethod.Get, $"v1/tasks/{taskId:D}/steps", null, ct);
    }

    private static async Task<McpToolText> StepAdd(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var title = Text(args, "title");
        if (string.IsNullOrWhiteSpace(title))
            return new McpToolText(true, "missing 'title'");
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/steps", new JsonObject { ["title"] = title }, ct);
    }

    private static async Task<McpToolText> StepToggle(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "stepId", out var stepId))
            return new McpToolText(true, "missing or invalid 'stepId'");
        var done = Flag(args, "done");
        if (done is null)
            return new McpToolText(true, "missing 'done'");
        return await SendAsync(api, HttpMethod.Patch, $"v1/steps/{stepId:D}", new JsonObject { ["done"] = done.Value }, ct);
    }

    private static async Task<McpToolText> StepDelete(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryGuid(args, "stepId", out var stepId))
            return new McpToolText(true, "missing or invalid 'stepId'");
        return await SendAsync(api, HttpMethod.Delete, $"v1/steps/{stepId:D}", null, ct);
    }

    private static async Task<McpToolText> Finish(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        var taskId = Text(args, "taskId");
        if (string.IsNullOrWhiteSpace(taskId))
            taskId = env.Get("BEACON_TASK_ID");
        if (string.IsNullOrWhiteSpace(taskId))
            return new McpToolText(true, "missing 'taskId' (or set BEACON_TASK_ID)");
        var result = Text(args, "result");
        if (string.IsNullOrWhiteSpace(result))
            return new McpToolText(true, "missing 'result'");
        var body = new JsonObject
        {
            ["taskId"] = taskId,
            ["result"] = result
        };
        Put(body, "output", Text(args, "output"));
        Put(body, "reviewTranscriptRef", Text(args, "reviewTranscriptRef"));
        Put(body, "reviewRunId", Text(args, "reviewRunId"));
        if (args?["review"] is JsonObject review)
            body["review"] = review.DeepClone();
        return await SendAsync(api, HttpMethod.Post, "v1/work/finish_work", body, ct);
    }

}