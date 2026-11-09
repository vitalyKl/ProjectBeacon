namespace ProjectBeacon.Domain.Common;

public static class PipelineToolPolicy
{
    public static IReadOnlyList<string> ReadOnlyWorkspaceTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.ReadFile, McpToolNames.GetTree, McpToolNames.SearchCode, McpToolNames.GetChangedScope,
        McpToolNames.GetSignatures, McpToolNames.GetCallers, McpToolNames.HashRange,
        McpToolNames.TaskListAttachments, McpToolNames.TaskReadAttachment
    });
    public static IReadOnlyList<string> ActorTerminalTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.SubtaskReportResult, McpToolNames.PipelineFailSubtask
    });
    public static IReadOnlyList<string> ActorReadOnlyAlwaysTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.TaskPipelineStatus
    });
    public static IReadOnlyList<string> AgentProfileKnowledgeWriteTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.ContextCreateDraft, McpToolNames.ConstraintPropose, McpToolNames.DecisionPropose
    });
    public static IReadOnlyList<string> PlannerSupervisorTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.ContextCompile, McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead,
        McpToolNames.TaskCreateSubtask, McpToolNames.TaskPipelineStatus, McpToolNames.PipelineStartReview
    });
    public static IReadOnlyList<string> ReviewerSupervisorTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.TaskPipelineStatus, McpToolNames.TaskReviewVerdict, McpToolNames.PipelineApprove
    });
    public static IReadOnlyList<string> PlannerTools { get; } = Array.AsReadOnly(ReadOnlyWorkspaceTools
        .Concat(new[]
        {
            McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead, McpToolNames.GetProject,
            McpToolNames.ListContextNodes, McpToolNames.GetContextNode, McpToolNames.ExportAgentsMd,
            McpToolNames.ListConstraints, McpToolNames.ListDecisions, McpToolNames.ListMilestones,
            McpToolNames.ListLabels, McpToolNames.MatchLabel, McpToolNames.WorkspaceWrite
        }).Concat(PlannerSupervisorTools).Distinct(StringComparer.Ordinal).ToArray());
    public static IReadOnlyList<string> ReviewerTools { get; } = Array.AsReadOnly(ReadOnlyWorkspaceTools
        .Concat(new[]
        {
            McpToolNames.GetProject, McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead,
            McpToolNames.ListContextNodes, McpToolNames.GetContextNode
        }).Concat(ReviewerSupervisorTools).Distinct(StringComparer.Ordinal).ToArray());
    public static IReadOnlyList<string> ActorWorkspaceTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.GetTask, McpToolNames.ListConstraints, McpToolNames.ListDecisions,
        McpToolNames.ListContextNodes, McpToolNames.GetContextNode,
        McpToolNames.TaskPipelineStatus, McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead,
        McpToolNames.WorkspaceWrite, McpToolNames.TaskAttachFile,
        McpToolNames.TaskListAttachments, McpToolNames.TaskReadAttachment,
        McpToolNames.ContextCreateDraft, McpToolNames.ConstraintPropose, McpToolNames.DecisionPropose
    });
    public static IReadOnlyList<string> ActorForbiddenTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.TaskCreateSubtask, McpToolNames.TaskReviewVerdict,
        McpToolNames.PipelineStart, McpToolNames.PipelineStartActor, McpToolNames.PipelineLaunchSession,
        McpToolNames.PipelineStartReview, McpToolNames.PipelineApprove, McpToolNames.PipelineForceClose,
        McpToolNames.ModelBind, McpToolNames.ModelStatus, McpToolNames.ModelUpsert, McpToolNames.ModelDelete, McpToolNames.ModelUnbind,
        McpToolNames.ProxyReload, McpToolNames.ProxyUnload,
        McpToolNames.CreateTask, McpToolNames.UpdateTask, McpToolNames.SetTaskStatus, McpToolNames.SetTaskSubstage,
        McpToolNames.ClaimTask, McpToolNames.AddTaskComment, McpToolNames.SetTaskDependencies, McpToolNames.AddReviewNotes,
        McpToolNames.ListTaskSteps, McpToolNames.AddTaskStep, McpToolNames.ToggleTaskStep, McpToolNames.DeleteTaskStep, McpToolNames.FinishWork,
        McpToolNames.UpsertContextNode, McpToolNames.DeleteContextNode,
        McpToolNames.CreateConstraint, McpToolNames.ActivateConstraint, McpToolNames.RejectConstraint,
        McpToolNames.RecordDecision, McpToolNames.AcceptDecision, McpToolNames.DeprecateDecision, McpToolNames.SupersedeDecision,
        McpToolNames.CreateMilestone, McpToolNames.UpdateMilestone, McpToolNames.DeleteMilestone, McpToolNames.CloseMilestone, McpToolNames.ReopenMilestone,
        McpToolNames.AddLabelPath, McpToolNames.GenerateReport
    });
    public static IReadOnlyList<string> AgentProfileReadTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.GetTree, McpToolNames.ReadFile, McpToolNames.SearchCode,
        McpToolNames.GetSignatures, McpToolNames.GetCallers, McpToolNames.GetChangedScope, McpToolNames.HashRange
    });
    public static IReadOnlyList<string> AgentProfileContextTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.GetTask, McpToolNames.TaskPipelineStatus, McpToolNames.ListConstraints,
        McpToolNames.ListDecisions, McpToolNames.ListContextNodes, McpToolNames.GetContextNode,
        McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead
    });
    public static IReadOnlyList<string> AgentProfileWriteTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.ApplyPatch, McpToolNames.WriteFile
    });
    public static IReadOnlyList<string> AgentProfileRequiredTools { get; } = Array.AsReadOnly(ActorTerminalTools
        .Concat(new[] { McpToolNames.WorkspaceWrite }).Distinct(StringComparer.Ordinal).ToArray());
    public static IReadOnlyList<string> InteractivePlanTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.ReadFile, McpToolNames.GetTree, McpToolNames.SearchCode, McpToolNames.GetChangedScope,
        McpToolNames.GetSignatures, McpToolNames.GetCallers, McpToolNames.HashRange,
        McpToolNames.GetProject, McpToolNames.ListTasks, McpToolNames.GetTask, McpToolNames.ListTaskSteps,
        McpToolNames.ListContextNodes, McpToolNames.GetContextNode, McpToolNames.ExportAgentsMd,
        McpToolNames.ListConstraints, McpToolNames.ListDecisions, McpToolNames.ListMilestones, McpToolNames.GetMilestone,
        McpToolNames.ListLabels, McpToolNames.MatchLabel, McpToolNames.ListReports, McpToolNames.GetReport,
        McpToolNames.TaskPipelineStatus, McpToolNames.ContextCompile, McpToolNames.ModelStatus, "suggest_command_prefixes",
        "run_log", "service_status", "service_logs"
    });
    public static IReadOnlyList<string> PipelinePlanDeniedTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.WriteFile, McpToolNames.ApplyPatch, "run_command", "service_start"
    });
}
