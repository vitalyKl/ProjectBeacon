namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Enums;

internal readonly record struct McpToolText(bool IsError, string Text);

internal static class McpApiTools
{
    private const string MissingApi =
        "BEACON_API_URL and BEACON_API_TOKEN are required. These tools call the Beacon /v1 API.";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "get_project",
        "list_tasks", "get_task", "create_task", "update_task", "set_task_status", "set_task_substage",
        "claim_task", "add_task_comment", "set_task_dependencies", "add_review_notes",
        "list_task_steps", "add_task_step", "toggle_task_step", "delete_task_step",
        "finish_work",
        "context_compile", "list_context_nodes", "get_context_node", "upsert_context_node", "delete_context_node",
        "export_agents_md", "list_constraints", "create_constraint", "activate_constraint", "reject_constraint",
        "list_decisions", "record_decision", "accept_decision", "deprecate_decision", "supersede_decision",
        "list_milestones", "get_milestone", "create_milestone", "update_milestone", "delete_milestone",
        "close_milestone", "reopen_milestone",
        "list_labels", "match_label", "add_label_path",
        "list_reports", "get_report", "generate_report",
        "pipeline_start", "pipeline_start_actor", "pipeline_launch_session", "pipeline_fail_subtask",
        "pipeline_start_review", "pipeline_approve", "pipeline_force_close",
        "model_upsert", "model_delete", "model_unbind", "proxy_reload", "proxy_unload"
    };

    public static bool IsApiTool(string name) => Names.Contains(name);

    public static IEnumerable<JsonObject> Definitions()
    {
        yield return Tool("get_project", "Get the current project from the Beacon API.",
            Props(("projectId", "string", false)));
        yield return Tool("list_tasks", "List tasks on the board and backlog. Optional status: Todo, InProgress, Done.",
            Props(("projectId", "string", false), ("status", "string", false)));
        yield return Tool("get_task", "Get one task, including comments and dependencies.",
            Props(("taskId", "string", false), ("projectId", "string", false)));
        yield return Tool("create_task", "Create a task. priority: Low, Medium, High, Critical. type: Feature, Bug, Improvement, Task.",
            Props(("title", "string", true), ("description", "string", false), ("priority", "string", false),
                ("type", "string", false), ("labelId", "string", false), ("milestoneId", "string", false),
                ("path", "string", false), ("projectId", "string", false)));
        yield return Tool("update_task", "Update a task title, description, priority, type, label, or milestone.",
            Props(("taskId", "string", true), ("title", "string", false), ("description", "string", false),
                ("priority", "string", false), ("type", "string", false), ("labelId", "string", false),
                ("milestoneId", "string", false), ("projectId", "string", false)));
        yield return Tool("set_task_status", "Move a task. status: Todo, InProgress, Done. Done still requires review notes.",
            Props(("taskId", "string", true), ("status", "string", true), ("projectId", "string", false)));
        yield return Tool("set_task_substage", "Set the in-progress sub-stage shown on the board.",
            Props(("taskId", "string", true), ("subStage", "string", true)));
        yield return Tool("claim_task", "Atomically claim a Todo task (Todo to InProgress). Uses BEACON_TASK_ID when taskId is omitted.",
            Props(("taskId", "string", false), ("projectId", "string", false)));
        yield return Tool("add_task_comment", "Add a comment on a task.",
            Props(("taskId", "string", true), ("content", "string", true), ("userId", "string", false)));
        yield return Tool("set_task_dependencies", "Replace the tasks this task depends on.",
            Props(("taskId", "string", true), ("dependentTaskIds", "array", true)));
        yield return Tool("add_review_notes", "Save review notes required before a task can move to Done.",
            Props(("taskId", "string", true), ("reviewNotes", "string", true)));
        yield return Tool("list_task_steps", "List checklist steps on a task.",
            Props(("taskId", "string", true)));
        yield return Tool("add_task_step", "Add a checklist step.",
            Props(("taskId", "string", true), ("title", "string", true)));
        yield return Tool("toggle_task_step", "Mark a checklist step done or not done.",
            Props(("stepId", "string", true), ("done", "boolean", true)));
        yield return Tool("delete_task_step", "Delete a checklist step.",
            Props(("stepId", "string", true)));
        yield return Tool("finish_work", "Finish work on a task. result: done, failed, skipped, or partial. done requires review.",
            FinishSchema());
        yield return Tool("context_compile", "Compile the project brief the same way the Context page does.",
            Props(("taskId", "string", false), ("path", "string", false), ("repoId", "string", false),
                ("budgetTokens", "integer", false), ("includeChangedScope", "boolean", false),
                ("includeTreeCapsule", "boolean", false), ("includeHandoff", "boolean", false),
                ("projectId", "string", false)));
        yield return Tool("list_context_nodes", "List context sections for the project.",
            Props(("projectId", "string", false)));
        yield return Tool("get_context_node", "Get one context section.",
            Props(("nodeId", "string", true), ("projectId", "string", false)));
        yield return Tool("upsert_context_node", "Create or update a context section. scopeType: Project, Repo, Path, Task. source defaults to Native.",
            Props(("title", "string", true), ("bodyMarkdown", "string", true), ("scopeType", "string", true),
                ("sectionId", "string", false), ("key", "string", false), ("path", "string", false),
                ("source", "string", false), ("sourcePath", "string", false), ("repoId", "string", false),
                ("taskId", "string", false), ("projectId", "string", false)));
        yield return Tool("delete_context_node", "Delete a context section.",
            Props(("nodeId", "string", true), ("projectId", "string", false)));
        yield return Tool("export_agents_md", "Export AGENTS.md markdown for the project.",
            Props(("repoId", "string", false), ("path", "string", false), ("projectId", "string", false)));
        yield return Tool("list_constraints", "List project constraints.",
            Props(("projectId", "string", false)));
        yield return Tool("create_constraint", "Propose a constraint. kind: Must, MustNot, Security, Compliance.",
            Props(("body", "string", true), ("kind", "string", true), ("projectId", "string", false)));
        yield return Tool("activate_constraint", "Activate a proposed constraint.",
            Props(("constraintId", "string", true), ("projectId", "string", false)));
        yield return Tool("reject_constraint", "Reject a proposed constraint.",
            Props(("constraintId", "string", true), ("projectId", "string", false)));
        yield return Tool("list_decisions", "List project decisions.",
            Props(("projectId", "string", false)));
        yield return Tool("record_decision", "Record a proposed decision.",
            Props(("title", "string", true), ("body", "string", true), ("context", "string", false),
                ("consequences", "string", false), ("projectId", "string", false)));
        yield return Tool("accept_decision", "Accept a proposed decision.",
            Props(("decisionId", "string", true), ("projectId", "string", false)));
        yield return Tool("deprecate_decision", "Deprecate a decision.",
            Props(("decisionId", "string", true), ("projectId", "string", false)));
        yield return Tool("supersede_decision", "Supersede an accepted decision with another decision.",
            Props(("decisionId", "string", true), ("replacementId", "string", true), ("projectId", "string", false)));
        yield return Tool("list_milestones", "List roadmap milestones.",
            Props(("projectId", "string", false)));
        yield return Tool("get_milestone", "Get one milestone.",
            Props(("milestoneId", "string", true)));
        yield return Tool("create_milestone", "Create a roadmap milestone.",
            Props(("name", "string", true), ("description", "string", false), ("order", "integer", false),
                ("projectId", "string", false)));
        yield return Tool("update_milestone", "Update a milestone name, description, or order.",
            Props(("milestoneId", "string", true), ("name", "string", false), ("description", "string", false),
                ("order", "integer", false)));
        yield return Tool("delete_milestone", "Delete a milestone.",
            Props(("milestoneId", "string", true), ("projectId", "string", false)));
        yield return Tool("close_milestone", "Close a milestone.",
            Props(("milestoneId", "string", true), ("projectId", "string", false)));
        yield return Tool("reopen_milestone", "Reopen a milestone.",
            Props(("milestoneId", "string", true), ("projectId", "string", false)));
        yield return Tool("list_labels", "List project area labels.",
            Props(("projectId", "string", false)));
        yield return Tool("match_label", "Match a file path to an active label.",
            Props(("path", "string", true), ("projectId", "string", false)));
        yield return Tool("add_label_path", "Add a path prefix to a label.",
            Props(("labelId", "string", true), ("path", "string", true), ("projectId", "string", false)));
        yield return Tool("list_reports", "List generated reports.",
            Props(("projectId", "string", false)));
        yield return Tool("get_report", "Get one report.",
            Props(("reportId", "string", true), ("projectId", "string", false)));
        yield return Tool("generate_report", "Generate a board snapshot report.",
            Props(("createdByType", "string", false), ("createdById", "string", false), ("projectId", "string", false)));
        yield return Tool("pipeline_start", "Start the task pipeline. Uses BEACON_TASK_ID when taskId is omitted.",
            Props(("taskId", "string", false)));
        yield return Tool("pipeline_start_actor", "Start an actor session for a subtask.",
            Props(("subtaskId", "string", true), ("taskId", "string", false)));
        yield return Tool("pipeline_launch_session", "Launch a pipeline session on the workstation.",
            Props(("sessionId", "string", true)));
        yield return Tool("pipeline_fail_subtask", "Fail an in-progress subtask.",
            Props(("subtaskId", "string", true), ("reason", "string", true), ("taskId", "string", false)));
        yield return Tool("pipeline_start_review", "Start the review stage of the pipeline.",
            Props(("taskId", "string", false)));
        yield return Tool("pipeline_approve", "Approve the pipeline and close the task.",
            Props(("note", "string", false), ("taskId", "string", false)));
        yield return Tool("pipeline_force_close", "Force-close a pipeline. The API token needs the Admin capability.",
            Props(("actorId", "string", true), ("reason", "string", false), ("taskId", "string", false)));
        yield return Tool("model_upsert", "Create or update a local model backend. backendType: FreeToken, LlamaCpp, OpenAiCompatible.",
            Props(("name", "string", true), ("backendType", "string", true), ("launchCommand", "string", true),
                ("contextSize", "integer", true), ("ttl", "integer", true), ("id", "string", false),
                ("extraFlags", "array", false), ("concurrent", "boolean", false)));
        yield return Tool("model_delete", "Delete a local model backend.",
            Props(("modelBackendId", "string", true)));
        yield return Tool("model_unbind", "Remove a pipeline role binding. role: planner, actor, review.",
            Props(("role", "string", true)));
        yield return Tool("proxy_reload", "Ask the workstation to reload llama-swap.", Props());
        yield return Tool("proxy_unload", "Ask the workstation to unload llama-swap.", Props());
    }

    public static Task<McpToolText> CallAsync(string name, JsonObject? args, BeaconApiClient? api, CancellationToken ct = default)
    {
        if (api is null)
            return Task.FromResult(new McpToolText(true, MissingApi));
        return InvokeAsync(name, args, api, ct);
    }

    public static Task<McpToolText> BindRoleAsync(PipelineRole role, Guid modelBackendId, BeaconApiClient api, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["role"] = role.ToString(),
            ["modelBackendId"] = modelBackendId.ToString("D")
        };
        return SendAsync(api, HttpMethod.Post, "v1/models/bind", body, ct);
    }

    public static async Task<McpToolText> ModelStatusAsync(BeaconApiClient api, CancellationToken ct = default)
    {
        var registry = await SendRawAsync(api, HttpMethod.Get, "v1/models", null, ct);
        if (registry.IsError)
            return registry;
        var proxy = await SendRawAsync(api, HttpMethod.Get, "v1/models/proxy/status", null, ct);
        JsonNode? registryNode;
        try
        {
            registryNode = JsonNode.Parse(registry.Text);
        }
        catch (JsonException)
        {
            return registry;
        }

        if (registryNode is not JsonObject obj)
            return registry;

        if (proxy.IsError)
            obj["proxy"] = new JsonObject { ["available"] = false, ["error"] = proxy.Text };
        else
        {
            try
            {
                obj["proxy"] = JsonNode.Parse(proxy.Text);
            }
            catch (JsonException)
            {
                obj["proxy"] = proxy.Text;
            }
        }

        return new McpToolText(false, obj.ToJsonString(Pretty));
    }

    public static Task<McpToolText> CreateSubtaskAsync(
        Guid taskId,
        string instructions,
        IReadOnlyList<string> allowedMcpTools,
        IReadOnlyList<string> allowedPaths,
        BeaconApiClient api,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["instructions"] = instructions,
            ["allowedMcpTools"] = StringArray(allowedMcpTools),
            ["allowedPaths"] = StringArray(allowedPaths)
        };
        return SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/subtasks", body, ct);
    }

    public static Task<McpToolText> ReportResultAsync(
        Guid taskId, Guid subtaskId, string diffRef, string summary, BeaconApiClient api, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["subtaskId"] = subtaskId.ToString("D"),
            ["diffRef"] = diffRef,
            ["summary"] = summary
        };
        return SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/subtasks/{subtaskId:D}/result", body, ct);
    }

    public static Task<McpToolText> ReviewVerdictAsync(
        Guid taskId, ReviewVerdictKind kind, string note, Guid? subtaskId, BeaconApiClient api, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["kind"] = kind.ToString(),
            ["note"] = note
        };
        if (subtaskId is not null)
            body["subtaskId"] = subtaskId.Value.ToString("D");
        return SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/verdict", body, ct);
    }

    public static Task<McpToolText> PipelineStatusAsync(Guid taskId, BeaconApiClient api, CancellationToken ct = default)
        => SendAsync(api, HttpMethod.Get, $"v1/tasks/{taskId:D}/pipeline", null, ct);

    private static async Task<McpToolText> InvokeAsync(string name, JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        try
        {
            return name switch
            {
                "get_project" => await ProjectGet(args, api, ct),
                "list_tasks" => await TasksList(args, api, ct),
                "get_task" => await TaskGet(args, api, ct),
                "create_task" => await TaskCreate(args, api, ct),
                "update_task" => await TaskUpdate(args, api, ct),
                "set_task_status" => await TaskStatus(args, api, ct),
                "set_task_substage" => await TaskSubStage(args, api, ct),
                "claim_task" => await TaskClaim(args, api, ct),
                "add_task_comment" => await TaskComment(args, api, ct),
                "set_task_dependencies" => await TaskDependencies(args, api, ct),
                "add_review_notes" => await TaskReviewNotes(args, api, ct),
                "list_task_steps" => await StepsList(args, api, ct),
                "add_task_step" => await StepAdd(args, api, ct),
                "toggle_task_step" => await StepToggle(args, api, ct),
                "delete_task_step" => await StepDelete(args, api, ct),
                "finish_work" => await Finish(args, api, ct),
                "context_compile" => await Compile(args, api, ct),
                "list_context_nodes" => await NodesList(args, api, ct),
                "get_context_node" => await NodeGet(args, api, ct),
                "upsert_context_node" => await NodeUpsert(args, api, ct),
                "delete_context_node" => await NodeDelete(args, api, ct),
                "export_agents_md" => await ExportAgents(args, api, ct),
                "list_constraints" => await ConstraintsList(args, api, ct),
                "create_constraint" => await ConstraintCreate(args, api, ct),
                "activate_constraint" => await ConstraintPost(args, "activate", api, ct),
                "reject_constraint" => await ConstraintPost(args, "reject", api, ct),
                "list_decisions" => await DecisionsList(args, api, ct),
                "record_decision" => await DecisionCreate(args, api, ct),
                "accept_decision" => await DecisionPost(args, "accept", api, ct),
                "deprecate_decision" => await DecisionPost(args, "deprecate", api, ct),
                "supersede_decision" => await DecisionSupersede(args, api, ct),
                "list_milestones" => await MilestonesList(args, api, ct),
                "get_milestone" => await MilestoneGet(args, api, ct),
                "create_milestone" => await MilestoneCreate(args, api, ct),
                "update_milestone" => await MilestoneUpdate(args, api, ct),
                "delete_milestone" => await MilestoneDelete(args, api, ct),
                "close_milestone" => await MilestoneAction(args, "close", api, ct),
                "reopen_milestone" => await MilestoneAction(args, "reopen", api, ct),
                "list_labels" => await LabelsList(args, api, ct),
                "match_label" => await LabelMatch(args, api, ct),
                "add_label_path" => await LabelPath(args, api, ct),
                "list_reports" => await ReportsList(args, api, ct),
                "get_report" => await ReportGet(args, api, ct),
                "generate_report" => await ReportGenerate(args, api, ct),
                "pipeline_start" => await PipelineStart(args, api, ct),
                "pipeline_start_actor" => await PipelineActor(args, api, ct),
                "pipeline_launch_session" => await PipelineLaunch(args, api, ct),
                "pipeline_fail_subtask" => await PipelineFail(args, api, ct),
                "pipeline_start_review" => await PipelineReview(args, api, ct),
                "pipeline_approve" => await PipelineApprove(args, api, ct),
                "pipeline_force_close" => await PipelineForceClose(args, api, ct),
                "model_upsert" => await ModelUpsert(args, api, ct),
                "model_delete" => await ModelDelete(args, api, ct),
                "model_unbind" => await ModelUnbind(args, api, ct),
                "proxy_reload" => await SendAsync(api, HttpMethod.Post, "v1/models/proxy/reload", new JsonObject(), ct),
                "proxy_unload" => await SendAsync(api, HttpMethod.Post, "v1/models/proxy/unload", new JsonObject(), ct),
                _ => new McpToolText(true, $"Unknown tool: {name}")
            };
        }
        catch (Exception ex)
        {
            return new McpToolText(true, ex.Message);
        }
    }

    private static async Task<McpToolText> ProjectGet(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}", null, ct);
    }

    private static async Task<McpToolText> TasksList(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        var status = Text(args, "status");
        var query = string.IsNullOrWhiteSpace(status) ? "" : "?status=" + Uri.EscapeDataString(status);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/tasks{query}", null, ct);
    }

    private static async Task<McpToolText> TaskGet(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryTask(args, out var taskId, out var error))
            return new McpToolText(true, error);
        if (TryProject(args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/tasks/{taskId:D}", null, ct);
        return await SendAsync(api, HttpMethod.Get, $"v1/tasks/{taskId:D}", null, ct);
    }

    private static async Task<McpToolText> TaskCreate(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        var title = Text(args, "title");
        if (string.IsNullOrWhiteSpace(title))
            return new McpToolText(true, "missing 'title'");
        if (!TryProject(args, out var projectId, out var error))
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

    private static async Task<McpToolText> TaskUpdate(JsonObject? args, BeaconApiClient api, CancellationToken ct)
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
        if (!TryProject(args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Put, $"v1/tasks/{taskId:D}", body, ct);
        return await SendAsync(api, HttpMethod.Put, $"v1/projects/{projectId:D}/tasks/{taskId:D}", body, ct);
    }

    private static async Task<McpToolText> TaskStatus(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var status = Text(args, "status");
        if (string.IsNullOrWhiteSpace(status))
            return new McpToolText(true, "missing 'status'");
        var body = new JsonObject { ["status"] = status };
        if (!TryProject(args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Patch, $"v1/tasks/{taskId:D}/status", body, ct);
        return await SendAsync(api, HttpMethod.Patch, $"v1/projects/{projectId:D}/tasks/{taskId:D}/status", body, ct);
    }

    private static async Task<McpToolText> TaskSubStage(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var subStage = Text(args, "subStage");
        if (string.IsNullOrWhiteSpace(subStage))
            return new McpToolText(true, "missing 'subStage'");
        return await SendAsync(api, HttpMethod.Patch, $"v1/tasks/{taskId:D}/substage", JsonValue.Create(subStage), ct);
    }

    private static async Task<McpToolText> TaskClaim(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryTask(args, out var taskId, out var error))
            return new McpToolText(true, error);
        if (!TryProject(args, out var projectId, out var projectError))
            return new McpToolText(true, projectError);
        return await SendAsync(api, HttpMethod.Patch, $"v1/projects/{projectId:D}/tasks/{taskId:D}/claim", null, ct);
    }

    private static async Task<McpToolText> TaskComment(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var content = Text(args, "content");
        if (string.IsNullOrWhiteSpace(content))
            return new McpToolText(true, "missing 'content'");
        var body = new JsonObject
        {
            ["taskId"] = taskId.ToString("D"),
            ["content"] = content,
            ["userId"] = Text(args, "userId") ?? Guid.Empty.ToString("D")
        };
        if (!TryProject(args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/comments", body, ct);
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/tasks/{taskId:D}/comments", body, ct);
    }

    private static async Task<McpToolText> TaskDependencies(JsonObject? args, BeaconApiClient api, CancellationToken ct)
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
        if (!TryProject(args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Put, $"v1/tasks/{taskId:D}/dependencies", body, ct);
        return await SendAsync(api, HttpMethod.Put, $"v1/projects/{projectId:D}/tasks/{taskId:D}/dependencies", body, ct);
    }

    private static async Task<McpToolText> TaskReviewNotes(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var notes = Text(args, "reviewNotes");
        if (string.IsNullOrWhiteSpace(notes))
            return new McpToolText(true, "missing 'reviewNotes'");
        if (!TryProject(args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Patch, $"v1/tasks/{taskId:D}/review-notes", JsonValue.Create(notes), ct);
        return await SendAsync(api, HttpMethod.Patch, $"v1/projects/{projectId:D}/tasks/{taskId:D}/review-notes", JsonValue.Create(notes), ct);
    }

    private static async Task<McpToolText> StepsList(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        return await SendAsync(api, HttpMethod.Get, $"v1/tasks/{taskId:D}/steps", null, ct);
    }

    private static async Task<McpToolText> StepAdd(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "taskId", out var taskId))
            return new McpToolText(true, "missing or invalid 'taskId'");
        var title = Text(args, "title");
        if (string.IsNullOrWhiteSpace(title))
            return new McpToolText(true, "missing 'title'");
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/steps", new JsonObject { ["title"] = title }, ct);
    }

    private static async Task<McpToolText> StepToggle(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "stepId", out var stepId))
            return new McpToolText(true, "missing or invalid 'stepId'");
        var done = Flag(args, "done");
        if (done is null)
            return new McpToolText(true, "missing 'done'");
        return await SendAsync(api, HttpMethod.Patch, $"v1/steps/{stepId:D}", new JsonObject { ["done"] = done.Value }, ct);
    }

    private static async Task<McpToolText> StepDelete(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "stepId", out var stepId))
            return new McpToolText(true, "missing or invalid 'stepId'");
        return await SendAsync(api, HttpMethod.Delete, $"v1/steps/{stepId:D}", null, ct);
    }

    private static async Task<McpToolText> Finish(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        var taskId = Text(args, "taskId");
        if (string.IsNullOrWhiteSpace(taskId))
            taskId = Environment.GetEnvironmentVariable("BEACON_TASK_ID");
        if (string.IsNullOrWhiteSpace(taskId))
            return new McpToolText(true, "missing 'taskId' (or set BEACON_TASK_ID)");
        var result = Text(args, "result");
        if (string.IsNullOrWhiteSpace(result))
            return new McpToolText(true, "missing 'result'");
        var actorId = Text(args, "actorId");
        if (string.IsNullOrWhiteSpace(actorId))
            return new McpToolText(true, "missing 'actorId'");
        var body = new JsonObject
        {
            ["taskId"] = taskId,
            ["result"] = result,
            ["actorId"] = actorId
        };
        Put(body, "output", Text(args, "output"));
        if (args?["review"] is JsonObject review)
            body["review"] = review.DeepClone();
        return await SendAsync(api, HttpMethod.Post, "v1/work/finish_work", body, ct);
    }

    private static async Task<McpToolText> Compile(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject
        {
            ["includeHandoff"] = Flag(args, "includeHandoff") ?? false,
            ["includeChangedScope"] = Flag(args, "includeChangedScope") ?? false,
            ["includeTreeCapsule"] = Flag(args, "includeTreeCapsule") ?? false
        };
        Put(body, "path", Text(args, "path"));
        Put(body, "repoId", Text(args, "repoId"));
        var taskId = Text(args, "taskId") ?? Environment.GetEnvironmentVariable("BEACON_TASK_ID");
        Put(body, "taskId", string.IsNullOrWhiteSpace(taskId) ? null : taskId);
        var budget = IntArg(args, "budgetTokens");
        if (budget is not null)
            body["budgetTokens"] = budget.Value;
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/context/compile", body, ct);
    }

    private static async Task<McpToolText> NodesList(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/context/nodes", null, ct);
    }

    private static async Task<McpToolText> NodeGet(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "nodeId", out var nodeId))
            return new McpToolText(true, "missing or invalid 'nodeId'");
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/context/nodes/{nodeId:D}", null, ct);
    }

    private static async Task<McpToolText> NodeUpsert(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        var title = Text(args, "title");
        var bodyMarkdown = Text(args, "bodyMarkdown");
        var scopeType = Text(args, "scopeType");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(bodyMarkdown) || string.IsNullOrWhiteSpace(scopeType))
            return new McpToolText(true, "title, bodyMarkdown, and scopeType are required");
        var body = new JsonObject
        {
            ["title"] = title,
            ["bodyMarkdown"] = bodyMarkdown,
            ["scopeType"] = scopeType,
            ["source"] = Text(args, "source") ?? "Native"
        };
        Put(body, "sectionId", Text(args, "sectionId"));
        Put(body, "key", Text(args, "key"));
        Put(body, "path", Text(args, "path"));
        Put(body, "sourcePath", Text(args, "sourcePath"));
        Put(body, "repoId", Text(args, "repoId"));
        Put(body, "taskId", Text(args, "taskId"));
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/context/nodes", body, ct);
    }

    private static async Task<McpToolText> NodeDelete(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "nodeId", out var nodeId))
            return new McpToolText(true, "missing or invalid 'nodeId'");
        return await SendAsync(api, HttpMethod.Delete, $"v1/projects/{projectId:D}/context/nodes/{nodeId:D}", null, ct);
    }

    private static async Task<McpToolText> ExportAgents(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        var query = new List<string>();
        var repoId = Text(args, "repoId");
        var path = Text(args, "path");
        if (!string.IsNullOrWhiteSpace(repoId))
            query.Add("repoId=" + Uri.EscapeDataString(repoId));
        if (!string.IsNullOrWhiteSpace(path))
            query.Add("path=" + Uri.EscapeDataString(path));
        var suffix = query.Count == 0 ? "" : "?" + string.Join("&", query);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/context/export/agents-md{suffix}", null, ct);
    }

    private static async Task<McpToolText> ConstraintsList(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/constraints", null, ct);
    }

    private static async Task<McpToolText> ConstraintCreate(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        var bodyText = Text(args, "body");
        var kind = Text(args, "kind");
        if (string.IsNullOrWhiteSpace(bodyText) || string.IsNullOrWhiteSpace(kind))
            return new McpToolText(true, "body and kind are required");
        var body = new JsonObject { ["body"] = bodyText, ["kind"] = kind };
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/constraints", body, ct);
    }

    private static async Task<McpToolText> ConstraintPost(JsonObject? args, string action, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "constraintId", out var constraintId))
            return new McpToolText(true, "missing or invalid 'constraintId'");
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/constraints/{constraintId:D}/{action}", new JsonObject(), ct);
    }

    private static async Task<McpToolText> DecisionsList(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/decisions", null, ct);
    }

    private static async Task<McpToolText> DecisionCreate(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        var title = Text(args, "title");
        var decisionBody = Text(args, "body");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(decisionBody))
            return new McpToolText(true, "title and body are required");
        var body = new JsonObject { ["title"] = title, ["body"] = decisionBody };
        Put(body, "context", Text(args, "context"));
        Put(body, "consequences", Text(args, "consequences"));
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/decisions", body, ct);
    }

    private static async Task<McpToolText> DecisionPost(JsonObject? args, string action, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "decisionId", out var decisionId))
            return new McpToolText(true, "missing or invalid 'decisionId'");
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/decisions/{decisionId:D}/{action}", new JsonObject(), ct);
    }

    private static async Task<McpToolText> DecisionSupersede(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "decisionId", out var decisionId))
            return new McpToolText(true, "missing or invalid 'decisionId'");
        if (!TryGuid(args, "replacementId", out var replacementId))
            return new McpToolText(true, "missing or invalid 'replacementId'");
        var body = new JsonObject { ["replacementId"] = replacementId.ToString("D") };
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/decisions/{decisionId:D}/supersede", body, ct);
    }

    private static async Task<McpToolText> MilestonesList(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/milestones", null, ct);
    }

    private static async Task<McpToolText> MilestoneGet(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "milestoneId", out var milestoneId))
            return new McpToolText(true, "missing or invalid 'milestoneId'");
        return await SendAsync(api, HttpMethod.Get, $"v1/milestones/{milestoneId:D}", null, ct);
    }

    private static async Task<McpToolText> MilestoneCreate(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        var name = Text(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return new McpToolText(true, "missing 'name'");
        var body = new JsonObject
        {
            ["name"] = name,
            ["projectId"] = projectId.ToString("D"),
            ["order"] = IntArg(args, "order") ?? 0
        };
        Put(body, "description", Text(args, "description"));
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/milestones", body, ct);
    }

    private static async Task<McpToolText> MilestoneUpdate(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "milestoneId", out var milestoneId))
            return new McpToolText(true, "missing or invalid 'milestoneId'");
        var body = new JsonObject { ["milestoneId"] = milestoneId.ToString("D") };
        Put(body, "name", Text(args, "name"));
        Put(body, "description", Text(args, "description"));
        var order = IntArg(args, "order");
        if (order is not null)
            body["order"] = order.Value;
        return await SendAsync(api, HttpMethod.Put, $"v1/milestones/{milestoneId:D}", body, ct);
    }

    private static async Task<McpToolText> MilestoneDelete(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "milestoneId", out var milestoneId))
            return new McpToolText(true, "missing or invalid 'milestoneId'");
        if (!TryProject(args, out var projectId, out _))
            return await SendAsync(api, HttpMethod.Delete, $"v1/milestones/{milestoneId:D}", null, ct);
        return await SendAsync(api, HttpMethod.Delete, $"v1/projects/{projectId:D}/milestones/{milestoneId:D}", null, ct);
    }

    private static async Task<McpToolText> MilestoneAction(JsonObject? args, string action, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "milestoneId", out var milestoneId))
            return new McpToolText(true, "missing or invalid 'milestoneId'");
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/milestones/{milestoneId:D}/{action}", new JsonObject(), ct);
    }

    private static async Task<McpToolText> LabelsList(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/labels", null, ct);
    }

    private static async Task<McpToolText> LabelMatch(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        var path = Text(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return new McpToolText(true, "missing 'path'");
        var response = await api.SendAsync(HttpMethod.Get, $"v1/projects/{projectId:D}/labels/match?path={Uri.EscapeDataString(path)}", null, ct);
        if (response.Status == 204)
            return new McpToolText(false, "No label matches this path.");
        return Format(response);
    }

    private static async Task<McpToolText> LabelPath(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "labelId", out var labelId))
            return new McpToolText(true, "missing or invalid 'labelId'");
        var path = Text(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            return new McpToolText(true, "missing 'path'");
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/labels/{labelId:D}/paths", new JsonObject { ["path"] = path }, ct);
    }

    private static async Task<McpToolText> ReportsList(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/reports", null, ct);
    }

    private static async Task<McpToolText> ReportGet(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "reportId", out var reportId))
            return new McpToolText(true, "missing or invalid 'reportId'");
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/reports/{reportId:D}", null, ct);
    }

    private static async Task<McpToolText> ReportGenerate(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryProject(args, out var projectId, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject();
        Put(body, "createdByType", Text(args, "createdByType") ?? "agent");
        Put(body, "createdById", Text(args, "createdById") ?? "mcp");
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/reports", body, ct);
    }

    private static async Task<McpToolText> PipelineStart(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryTask(args, out var taskId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/start", new JsonObject { ["taskId"] = taskId.ToString("D") }, ct);
    }

    private static async Task<McpToolText> PipelineActor(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryTask(args, out var taskId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "subtaskId", out var subtaskId))
            return new McpToolText(true, "missing or invalid 'subtaskId'");
        var body = new JsonObject { ["taskId"] = taskId.ToString("D"), ["subtaskId"] = subtaskId.ToString("D") };
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/subtasks/{subtaskId:D}/session", body, ct);
    }

    private static async Task<McpToolText> PipelineLaunch(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "sessionId", out var sessionId))
            return new McpToolText(true, "missing or invalid 'sessionId'");
        return await SendAsync(api, HttpMethod.Post, $"v1/sessions/{sessionId:D}/launch", new JsonObject { ["sessionId"] = sessionId.ToString("D") }, ct);
    }

    private static async Task<McpToolText> PipelineFail(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryTask(args, out var taskId, out var error))
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

    private static async Task<McpToolText> PipelineReview(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryTask(args, out var taskId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/review/start", new JsonObject { ["taskId"] = taskId.ToString("D") }, ct);
    }

    private static async Task<McpToolText> PipelineApprove(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryTask(args, out var taskId, out var error))
            return new McpToolText(true, error);
        var body = new JsonObject { ["taskId"] = taskId.ToString("D") };
        Put(body, "note", Text(args, "note"));
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/approve", body, ct);
    }

    private static async Task<McpToolText> PipelineForceClose(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryTask(args, out var taskId, out var error))
            return new McpToolText(true, error);
        var actorId = Text(args, "actorId");
        if (string.IsNullOrWhiteSpace(actorId))
            return new McpToolText(true, "missing 'actorId'");
        var body = new JsonObject { ["taskId"] = taskId.ToString("D"), ["actorId"] = actorId };
        Put(body, "reason", Text(args, "reason"));
        return await SendAsync(api, HttpMethod.Post, $"v1/tasks/{taskId:D}/pipeline/force-close", body, ct);
    }

    private static async Task<McpToolText> ModelUpsert(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        var name = Text(args, "name");
        var backendType = Text(args, "backendType");
        var launchCommand = Text(args, "launchCommand");
        var contextSize = IntArg(args, "contextSize");
        var ttl = IntArg(args, "ttl");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(backendType) || string.IsNullOrWhiteSpace(launchCommand)
            || contextSize is null || ttl is null)
            return new McpToolText(true, "name, backendType, launchCommand, contextSize, and ttl are required");
        var body = new JsonObject
        {
            ["name"] = name,
            ["backendType"] = backendType,
            ["launchCommand"] = launchCommand,
            ["contextSize"] = contextSize.Value,
            ["ttl"] = ttl.Value,
            ["concurrent"] = Flag(args, "concurrent") ?? false,
            ["extraFlags"] = StringArray(Strings(args, "extraFlags"))
        };
        Put(body, "id", Text(args, "id"));
        return await SendAsync(api, HttpMethod.Post, "v1/models", body, ct);
    }

    private static async Task<McpToolText> ModelDelete(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        if (!TryGuid(args, "modelBackendId", out var id))
            return new McpToolText(true, "missing or invalid 'modelBackendId'");
        return await SendAsync(api, HttpMethod.Delete, $"v1/models/{id:D}", null, ct);
    }

    private static async Task<McpToolText> ModelUnbind(JsonObject? args, BeaconApiClient api, CancellationToken ct)
    {
        var role = RoleName(Text(args, "role"));
        if (role is null)
            return new McpToolText(true, "missing or invalid 'role' (expected planner, actor or review)");
        return await SendAsync(api, HttpMethod.Delete, $"v1/models/bind/{role}", null, ct);
    }

    private static async Task<McpToolText> SendAsync(
        BeaconApiClient api, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
        => Format(await api.SendAsync(method, path, body, ct));

    private static async Task<McpToolText> SendRawAsync(
        BeaconApiClient api, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var (ok, status, raw) = await api.SendAsync(method, path, body, ct);
        if (!ok)
            return Fail(status, raw);
        return new McpToolText(false, raw);
    }

    private static McpToolText Format((bool Ok, int Status, string Body) response)
        => response.Ok ? Ok(response.Body, response.Status) : Fail(response.Status, response.Body);

    private static McpToolText Ok(string body, int status)
    {
        if (status == 204 || string.IsNullOrWhiteSpace(body))
            return new McpToolText(false, "ok");
        var trimmed = body.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return new McpToolText(false, JsonNode.Parse(trimmed)!.ToJsonString(Pretty));
            }
            catch (JsonException)
            {
            }
        }

        return new McpToolText(false, body);
    }

    private static McpToolText Fail(int status, string body)
    {
        if (status == 204)
            return new McpToolText(true, "HTTP 204");
        var trimmed = body.Trim();
        if (trimmed.Length > 2000)
            trimmed = trimmed[..2000];
        try
        {
            var error = JsonNode.Parse(trimmed)?["error"];
            if (error is JsonValue value && value.TryGetValue<string>(out var message) && !string.IsNullOrWhiteSpace(message))
                return new McpToolText(true, $"HTTP {status}: {message}");
        }
        catch (JsonException)
        {
        }

        return new McpToolText(true, string.IsNullOrEmpty(trimmed) ? $"HTTP {status}" : $"HTTP {status}: {trimmed}");
    }

    private static bool TryProject(JsonObject? args, out Guid projectId, out string error)
    {
        var raw = Text(args, "projectId");
        if (string.IsNullOrWhiteSpace(raw))
            raw = Environment.GetEnvironmentVariable("BEACON_PROJECT_ID");
        if (Guid.TryParse(raw, out projectId))
        {
            error = "";
            return true;
        }

        projectId = Guid.Empty;
        error = "BEACON_PROJECT_ID is not set (or is not a GUID). Set it to the project GUID for this MCP session.";
        return false;
    }

    private static bool TryTask(JsonObject? args, out Guid taskId, out string error)
    {
        var raw = Text(args, "taskId");
        if (string.IsNullOrWhiteSpace(raw))
            raw = Environment.GetEnvironmentVariable("BEACON_TASK_ID");
        if (Guid.TryParse(raw, out taskId))
        {
            error = "";
            return true;
        }

        taskId = Guid.Empty;
        error = "missing 'taskId' (or set BEACON_TASK_ID).";
        return false;
    }

    private static bool TryGuid(JsonObject? args, string key, out Guid id)
        => Guid.TryParse(Text(args, key), out id);

    private static bool TryGuidList(JsonObject? args, string key, out List<Guid> ids, out string error)
    {
        ids = [];
        var node = args?[key];
        if (node is null)
        {
            error = $"missing '{key}'";
            return false;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var text = item?.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : null;
                if (!Guid.TryParse(text, out var id))
                {
                    error = $"invalid guid in '{key}'";
                    return false;
                }

                ids.Add(id);
            }

            error = "";
            return true;
        }

        var raw = Text(args, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"missing '{key}'";
            return false;
        }

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id))
            {
                error = $"invalid guid in '{key}'";
                return false;
            }

            ids.Add(id);
        }

        error = "";
        return true;
    }

    private static string? Text(JsonObject? args, string key)
    {
        var node = args?[key];
        if (node is null || node.GetValueKind() != JsonValueKind.String)
            return null;
        return node.GetValue<string>();
    }

    private static bool? Flag(JsonObject? args, string key)
    {
        var node = args?[key];
        if (node is null)
            return null;
        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? IntArg(JsonObject? args, string key)
    {
        var node = args?[key];
        if (node is null)
            return null;
        if (node.GetValueKind() == JsonValueKind.Number && node is JsonValue number && number.TryGetValue<int>(out var value))
            return value;
        if (node.GetValueKind() == JsonValueKind.String && int.TryParse(node.GetValue<string>(), out var parsed))
            return parsed;
        return null;
    }

    private static IReadOnlyList<string> Strings(JsonObject? args, string key)
    {
        var node = args?[key];
        if (node is JsonArray array)
        {
            return array
                .Select(item => item?.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : null)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();
        }

        var text = Text(args, key);
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static JsonArray StringArray(IReadOnlyList<string> values)
        => new(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());

    private static string? RoleName(string? raw) => raw?.ToLowerInvariant() switch
    {
        "planner" => "Planner",
        "actor" => "Actor",
        "review" => "Review",
        _ => null
    };

    private static void Put(JsonObject body, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            body[key] = value;
    }

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

    private static JsonObject FinishSchema()
    {
        var schema = Props(
            ("taskId", "string", false),
            ("result", "string", true),
            ("output", "string", false),
            ("actorId", "string", true));
        var properties = schema["properties"]!.AsObject();
        properties["review"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reviewerRun"] = new JsonObject { ["type"] = "boolean" },
                ["regressionsFound"] = new JsonObject { ["type"] = "integer" },
                ["regressionsFixed"] = new JsonObject { ["type"] = "integer" }
            }
        };
        return schema;
    }
}
