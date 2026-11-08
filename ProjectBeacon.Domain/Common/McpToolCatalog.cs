namespace ProjectBeacon.Domain.Common;

using ProjectBeacon.Domain.Enums;

/// <summary>Authorization scope documented for a built-in Beacon MCP tool.</summary>
public enum McpToolScope
{
    Read,
    Write,
    Admin
}

/// <summary>Surfaces a built-in MCP tool belongs to; flags may be combined.</summary>
[Flags]
public enum McpToolSurface
{
    None = 0,
    ControlPlane = 1,
    Workspace = 2,
    Pipeline = 4,
    ModelManagement = 8
}

/// <summary>Dispatcher that owns a built-in MCP tool implementation.</summary>
public enum McpToolHandler
{
    Api,
    Workspace,
    Session
}

/// <summary>
/// Canonical name and cross-layer metadata for a built-in ProjectBeacon MCP tool.
/// Aliases are accepted for persisted allowlists; this descriptor advertises scope but
/// never replaces the server-side API authorization boundary.
/// </summary>
public sealed record McpToolDescriptor(
    string Name,
    IReadOnlyList<string> Aliases,
    ApiTokenCapability Capability,
    McpToolScope Scope,
    McpToolSurface Surface,
    McpToolHandler Handler);

/// <summary>
/// Canonical tool-name constants. Use these rather than repeating string literals in
/// definitions, role allowlists, and native runtime policies.
/// </summary>
public static class McpToolNames
{
    public const string GetProject = "get_project";
    public const string ListTasks = "list_tasks";
    public const string GetTask = "get_task";
    public const string CreateTask = "create_task";
    public const string UpdateTask = "update_task";
    public const string SetTaskStatus = "set_task_status";
    public const string SetTaskSubstage = "set_task_substage";
    public const string ClaimTask = "claim_task";
    public const string AddTaskComment = "add_task_comment";
    public const string TaskAttachFile = "task_attach_file";
    public const string TaskListAttachments = "task_list_attachments";
    public const string TaskReadAttachment = "task_read_attachment";
    public const string SetTaskDependencies = "set_task_dependencies";
    public const string AddReviewNotes = "add_review_notes";
    public const string ListTaskSteps = "list_task_steps";
    public const string AddTaskStep = "add_task_step";
    public const string ToggleTaskStep = "toggle_task_step";
    public const string DeleteTaskStep = "delete_task_step";
    public const string FinishWork = "finish_work";
    public const string ContextCompile = "context_compile";
    public const string ListContextNodes = "list_context_nodes";
    public const string GetContextNode = "get_context_node";
    public const string UpsertContextNode = "upsert_context_node";
    public const string DeleteContextNode = "delete_context_node";
    public const string ExportAgentsMd = "export_agents_md";
    public const string ListConstraints = "list_constraints";
    public const string CreateConstraint = "create_constraint";
    public const string ActivateConstraint = "activate_constraint";
    public const string RejectConstraint = "reject_constraint";
    public const string ListDecisions = "list_decisions";
    public const string RecordDecision = "record_decision";
    public const string AcceptDecision = "accept_decision";
    public const string DeprecateDecision = "deprecate_decision";
    public const string SupersedeDecision = "supersede_decision";
    public const string ListMilestones = "list_milestones";
    public const string GetMilestone = "get_milestone";
    public const string CreateMilestone = "create_milestone";
    public const string UpdateMilestone = "update_milestone";
    public const string DeleteMilestone = "delete_milestone";
    public const string CloseMilestone = "close_milestone";
    public const string ReopenMilestone = "reopen_milestone";
    public const string ListLabels = "list_labels";
    public const string MatchLabel = "match_label";
    public const string AddLabelPath = "add_label_path";
    public const string ListReports = "list_reports";
    public const string GetReport = "get_report";
    public const string GenerateReport = "generate_report";
    public const string PipelineStart = "pipeline_start";
    public const string PipelineStartActor = "pipeline_start_actor";
    public const string PipelineLaunchSession = "pipeline_launch_session";
    public const string PipelineFailSubtask = "pipeline_fail_subtask";
    public const string PipelineStartReview = "pipeline_start_review";
    public const string PipelineApprove = "pipeline_approve";
    public const string PipelineForceClose = "pipeline_force_close";
    public const string ModelUpsert = "model_upsert";
    public const string ModelDelete = "model_delete";
    public const string ModelUnbind = "model_unbind";
    public const string ProxyReload = "proxy_reload";
    public const string ProxyUnload = "proxy_unload";
    public const string ReadFile = "read_file";
    public const string WriteFile = "write_file";
    public const string ApplyPatch = "apply_patch";
    public const string GetTree = "get_tree";
    public const string SearchCode = "search_code";
    public const string GetChangedScope = "get_changed_scope";
    public const string GetSignatures = "get_signatures";
    public const string GetCallers = "get_callers";
    public const string HashRange = "hash_range";
    public const string ModelBind = "model_bind";
    public const string ModelStatus = "model_status";
    public const string TaskCreateSubtask = "task_create_subtask";
    public const string SubtaskReportResult = "subtask_report_result";
    public const string TaskReviewVerdict = "task_review_verdict";
    public const string TaskPipelineStatus = "task_pipeline_status";
    public const string WorkspaceIndex = "workspace_index";
    public const string WorkspaceRead = "workspace_read";
    public const string WorkspaceWrite = "workspace_write";
}

/// <summary>
/// Authoritative registry of built-in ProjectBeacon MCP tools. It deliberately excludes
/// third-party MCP tools, whose names are supplied dynamically by their server configuration.
/// </summary>
public static class McpToolCatalog
{
    private static readonly IReadOnlyDictionary<string, McpToolDescriptor> ByName = Build();
    private static readonly IReadOnlyDictionary<string, McpToolDescriptor> ByAlias =
        ByName.Values.SelectMany(descriptor => descriptor.Aliases.Select(alias => (alias, descriptor)))
            .ToDictionary(pair => pair.alias, pair => pair.descriptor, StringComparer.Ordinal);

    public static IReadOnlyCollection<McpToolDescriptor> All => ByName.Values.ToArray();

    public static McpToolDescriptor Get(string name) =>
        ByName.TryGetValue(name, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"MCP tool '{name}' is not registered.");

    public static bool TryGet(string? name, out McpToolDescriptor? descriptor)
    {
        descriptor = null;
        return !string.IsNullOrWhiteSpace(name) && ByName.TryGetValue(name, out descriptor);
    }

    public static bool TryResolve(string? name, out McpToolDescriptor? descriptor)
    {
        descriptor = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return ByName.TryGetValue(name, out descriptor) || ByAlias.TryGetValue(name, out descriptor);
    }

    public static bool IsControlPlaneTool(string name) =>
        TryGet(name, out var descriptor)
        && (descriptor!.Surface & McpToolSurface.ControlPlane) != 0;

    private static IReadOnlyDictionary<string, McpToolDescriptor> Build()
    {
        McpToolDescriptor[] descriptors =
        [
            // Task and project API tools.
            ..Api(McpToolScope.Read, ApiTokenCapability.TaskRead,
                McpToolNames.GetProject, McpToolNames.ListTasks, McpToolNames.GetTask, McpToolNames.ListTaskSteps,
                McpToolNames.TaskListAttachments, McpToolNames.TaskReadAttachment),
            ..Api(McpToolScope.Read, ApiTokenCapability.TaskRead,
                McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead),
            ..Api(McpToolScope.Write, ApiTokenCapability.TaskWrite,
                McpToolNames.WorkspaceWrite),
            ..Api(McpToolScope.Write, ApiTokenCapability.TaskWrite,
                McpToolNames.CreateTask, McpToolNames.UpdateTask, McpToolNames.SetTaskStatus,
                McpToolNames.SetTaskSubstage, McpToolNames.ClaimTask, McpToolNames.AddTaskComment,
                McpToolNames.TaskAttachFile,
                McpToolNames.SetTaskDependencies, McpToolNames.AddReviewNotes, McpToolNames.AddTaskStep,
                McpToolNames.ToggleTaskStep, McpToolNames.DeleteTaskStep, McpToolNames.FinishWork),

            // Context and governance tools use the explicit context capability.
            ..Api(McpToolScope.Read, ApiTokenCapability.ContextRead,
                McpToolNames.ContextCompile, McpToolNames.ListContextNodes, McpToolNames.GetContextNode,
                McpToolNames.ExportAgentsMd, McpToolNames.ListConstraints, McpToolNames.ListDecisions),
            ..Api(McpToolScope.Write, ApiTokenCapability.ContextRead,
                McpToolNames.UpsertContextNode, McpToolNames.DeleteContextNode, McpToolNames.CreateConstraint,
                McpToolNames.ActivateConstraint, McpToolNames.RejectConstraint, McpToolNames.RecordDecision,
                McpToolNames.AcceptDecision, McpToolNames.DeprecateDecision, McpToolNames.SupersedeDecision),

            // Catalog and roadmap reads/writes.
            ..Api(McpToolScope.Read, ApiTokenCapability.TaskRead,
                McpToolNames.ListMilestones, McpToolNames.GetMilestone, McpToolNames.ListLabels,
                McpToolNames.MatchLabel, McpToolNames.ListReports, McpToolNames.GetReport),
            ..Api(McpToolScope.Write, ApiTokenCapability.TaskWrite,
                McpToolNames.CreateMilestone, McpToolNames.UpdateMilestone, McpToolNames.DeleteMilestone,
                McpToolNames.CloseMilestone, McpToolNames.ReopenMilestone, McpToolNames.AddLabelPath,
                McpToolNames.GenerateReport),

            // Pipeline control tools; the HTTP API still checks token capability and role scope.
            ..Api(McpToolScope.Write, ApiTokenCapability.TaskWrite, McpToolSurface.Pipeline,
                McpToolNames.PipelineStart, McpToolNames.PipelineStartActor, McpToolNames.PipelineLaunchSession,
                McpToolNames.PipelineFailSubtask, McpToolNames.PipelineStartReview, McpToolNames.PipelineApprove),
            ..Api(McpToolScope.Admin, ApiTokenCapability.Admin, McpToolSurface.Pipeline,
                McpToolNames.PipelineForceClose),

            // Local model management is an administrative surface.
            ..Api(McpToolScope.Admin, ApiTokenCapability.Admin, McpToolSurface.ModelManagement,
                McpToolNames.ModelUpsert, McpToolNames.ModelDelete, McpToolNames.ModelUnbind,
                McpToolNames.ProxyReload, McpToolNames.ProxyUnload),

            // Workspace tools run locally under the configured project root.
            ..Workspace(McpToolScope.Read,
                McpToolNames.ReadFile, McpToolNames.GetTree, McpToolNames.SearchCode,
                McpToolNames.GetChangedScope, McpToolNames.GetSignatures, McpToolNames.GetCallers,
                McpToolNames.HashRange),
            ..Workspace(McpToolScope.Write, McpToolNames.WriteFile, McpToolNames.ApplyPatch),

            // Session tools are dispatched by the MCP server's session handlers.
            ..Session(McpToolScope.Admin, ApiTokenCapability.Admin, McpToolSurface.ModelManagement,
                McpToolNames.ModelBind),
            ..Session(McpToolScope.Read, ApiTokenCapability.TaskRead,
                McpToolNames.ModelStatus, McpToolNames.TaskPipelineStatus),
            ..Session(McpToolScope.Write, ApiTokenCapability.TaskWrite, McpToolSurface.Pipeline,
                McpToolNames.TaskCreateSubtask, McpToolNames.SubtaskReportResult, McpToolNames.TaskReviewVerdict)
        ];

        var duplicateNames = descriptors.GroupBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateNames.Length > 0)
            throw new InvalidOperationException($"Duplicate MCP tool names: {string.Join(", ", duplicateNames)}.");

        var duplicateAliases = descriptors.SelectMany(descriptor => descriptor.Aliases)
            .GroupBy(alias => alias, StringComparer.Ordinal)
            .Where(group => group.Count() > 1 || descriptors.Any(descriptor => descriptor.Name == group.Key))
            .Select(group => group.Key)
            .ToArray();
        if (duplicateAliases.Length > 0)
            throw new InvalidOperationException($"Duplicate MCP tool aliases: {string.Join(", ", duplicateAliases)}.");

        return descriptors.ToDictionary(descriptor => descriptor.Name, StringComparer.Ordinal);
    }

    private static IEnumerable<McpToolDescriptor> Api(
        McpToolScope scope,
        ApiTokenCapability capability,
        params string[] names) =>
        Api(scope, capability, McpToolSurface.ControlPlane, names);

    private static IEnumerable<McpToolDescriptor> Api(
        McpToolScope scope,
        ApiTokenCapability capability,
        McpToolSurface surface,
        params string[] names) =>
        names.Select(name => Descriptor(name, scope, capability, surface | McpToolSurface.ControlPlane, McpToolHandler.Api));

    private static IEnumerable<McpToolDescriptor> Workspace(McpToolScope scope, params string[] names) =>
        names.Select(name => Descriptor(name, scope, ApiTokenCapability.None, McpToolSurface.Workspace, McpToolHandler.Workspace));

    private static IEnumerable<McpToolDescriptor> Session(
        McpToolScope scope,
        ApiTokenCapability capability,
        params string[] names) =>
        Session(scope, capability, McpToolSurface.ControlPlane, names);

    private static IEnumerable<McpToolDescriptor> Session(
        McpToolScope scope,
        ApiTokenCapability capability,
        McpToolSurface surface,
        params string[] names) =>
        names.Select(name => Descriptor(name, scope, capability, surface | McpToolSurface.ControlPlane, McpToolHandler.Session));

    private static McpToolDescriptor Descriptor(
        string name,
        McpToolScope scope,
        ApiTokenCapability capability,
        McpToolSurface surface,
        McpToolHandler handler) =>
        new(name, [$"beacon_{name}"], capability, scope, surface, handler);
}
