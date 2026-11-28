namespace ProjectBeacon.Domain.Common;

public static class PipelineToolPolicy
{
    public const string GitInfo = "git_info";
    public const string GitCommit = "git_commit";
    public const string GitIntegrate = "git_integrate";
    public const string GitPublish = "git_publish";
    public const string GitPullRequest = "git_pr";
    public const string ForgeRead = "forge_read";

    public static IReadOnlyList<string> PlannerGitTools { get; } = Array.AsReadOnly(new[]
    {
        GitInfo, GitIntegrate, GitPullRequest, ForgeRead
    });
    /// <summary>Default Actor Git tools. Publishing is excluded unless a trusted authorization explicitly opts in.</summary>
    public static IReadOnlyList<string> ActorGitTools { get; } = Array.AsReadOnly(new[]
    {
        GitInfo, GitCommit, GitIntegrate, GitPullRequest, ForgeRead
    });
    public static IReadOnlyList<string> ReviewerGitTools { get; } = Array.AsReadOnly(new[]
    {
        GitInfo, GitPullRequest, ForgeRead
    });

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> GitToolsByRole =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Planner"] = PlannerGitTools,
                ["Actor"] = ActorGitTools,
                ["Review"] = ReviewerGitTools
            });

    public static IReadOnlyList<string> GitToolsForRole(string? role) =>
        role is not null && GitToolsByRole.TryGetValue(role, out var tools)
            ? tools
            : Array.Empty<string>();

    /// <summary>
    /// Adds task-branch publishing only when a trusted server-issued authorization explicitly
    /// grants it. The default is false and the normal Actor role ceiling excludes publishing.
    /// </summary>
    public static IReadOnlyList<string> GitToolsForAuthorization(
        string? role,
        bool allowAgentGitPublish = false)
    {
        var roleTools = GitToolsForRole(role);
        if (!allowAgentGitPublish
            || !string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase))
            return roleTools;
        return Array.AsReadOnly(roleTools.Append(GitPublish).ToArray());
    }

    public static bool IsGitToolAllowed(
        string? role,
        string? toolName,
        bool allowAgentGitPublish = false) =>
        toolName is not null
        && GitToolsForAuthorization(role, allowAgentGitPublish)
            .Contains(toolName, StringComparer.Ordinal);
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
        McpToolNames.ContextCreateDraft, McpToolNames.ConstraintPropose, McpToolNames.DecisionPropose,
        McpToolNames.KnowledgePropose, McpToolNames.KnowledgeProposals, McpToolNames.KnowledgeWithdraw
    });
    public static IReadOnlyList<string> PlannerSupervisorTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.ContextCompile, McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead,
        McpToolNames.TaskCreateSubtask, McpToolNames.TaskPipelineStatus, McpToolNames.PipelineStartReview,
        McpToolNames.PipelineLandRequest, McpToolNames.PipelineLandRevert, McpToolNames.PipelineLandStatus
    });
    public static IReadOnlyList<string> ReviewerSupervisorTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.TaskPipelineStatus, McpToolNames.TaskReviewVerdict,
        McpToolNames.PipelineLandRequest, McpToolNames.PipelineLandStatus,
        McpToolNames.PipelineApprove, McpToolNames.GetTaskVersionImpact,
        McpToolNames.VersionCheck, McpToolNames.ReleaseContext, McpToolNames.ReviewVersionImpact
    });
    public static IReadOnlyList<string> PlannerTools { get; } = Array.AsReadOnly(ReadOnlyWorkspaceTools
        .Concat(new[]
        {
            McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead, McpToolNames.GetProject, McpToolNames.VersionCheck, McpToolNames.ReleaseContext, "repo_status",
            McpToolNames.ListContextNodes, McpToolNames.GetContextNode, McpToolNames.ExportAgentsMd,
            McpToolNames.ListConstraints, McpToolNames.GetConstraint, McpToolNames.ListDecisions,
            McpToolNames.GetDecision, McpToolNames.ListMilestones,
            McpToolNames.GetTaskVersionImpact,
            McpToolNames.ListLabels, McpToolNames.MatchLabel, McpToolNames.WorkspaceWrite
        }).Concat(PlannerSupervisorTools).Distinct(StringComparer.Ordinal).ToArray());
    public static IReadOnlyList<string> ReviewerTools { get; } = Array.AsReadOnly(ReadOnlyWorkspaceTools
        .Concat(new[]
        {
            McpToolNames.GetProject, McpToolNames.VersionCheck, McpToolNames.ReleaseContext, McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead,
            McpToolNames.ListContextNodes, McpToolNames.GetContextNode,
            McpToolNames.ListConstraints, McpToolNames.GetConstraint,
            McpToolNames.ListDecisions, McpToolNames.GetDecision,
            McpToolNames.GetTaskVersionImpact
        }).Concat(ReviewerSupervisorTools).Distinct(StringComparer.Ordinal).ToArray());
    public static IReadOnlyList<string> ActorWorkspaceTools { get; } = Array.AsReadOnly(new[]
    {
        "repo_status", McpToolNames.GetTask, McpToolNames.VersionCheck, McpToolNames.VersionImpact, McpToolNames.ReleaseContext, McpToolNames.ListConstraints, McpToolNames.GetConstraint,
        McpToolNames.ListDecisions, McpToolNames.GetDecision,
        McpToolNames.ListContextNodes, McpToolNames.GetContextNode,
        McpToolNames.TaskPipelineStatus, McpToolNames.WorkspaceIndex, McpToolNames.WorkspaceRead,
        McpToolNames.GetTaskVersionImpact,
        McpToolNames.WorkspaceWrite, McpToolNames.TaskAttachFile,
        McpToolNames.TaskListAttachments, McpToolNames.TaskReadAttachment,
        McpToolNames.ContextCreateDraft, McpToolNames.ConstraintPropose, McpToolNames.DecisionPropose,
        McpToolNames.KnowledgePropose, McpToolNames.KnowledgeProposals, McpToolNames.KnowledgeWithdraw,
        McpToolNames.HashFile, McpToolNames.HashText
    });
    public static IReadOnlyList<string> ActorForbiddenTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.TaskCreateSubtask, McpToolNames.TaskReviewVerdict, McpToolNames.ReviewVersionImpact,
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
        McpToolNames.GetConstraint, McpToolNames.ListDecisions, McpToolNames.GetDecision,
        McpToolNames.ListContextNodes, McpToolNames.GetContextNode,
        McpToolNames.TaskListAttachments, McpToolNames.TaskReadAttachment,
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
        McpToolNames.ListConstraints, McpToolNames.GetConstraint,
        McpToolNames.ListDecisions, McpToolNames.GetDecision,
        McpToolNames.ListMilestones, McpToolNames.GetMilestone,
        McpToolNames.ListLabels, McpToolNames.MatchLabel, McpToolNames.ListReports, McpToolNames.GetReport,
        McpToolNames.TaskPipelineStatus, McpToolNames.ContextCompile, McpToolNames.ModelStatus, "repo_status", "suggest_command_prefixes",
        GitInfo, ForgeRead, "run_log", "service_status", "service_logs"
    });
    public static IReadOnlyList<string> PipelinePlanDeniedTools { get; } = Array.AsReadOnly(new[]
    {
        McpToolNames.WriteFile, McpToolNames.ApplyPatch, McpToolNames.PipelineLandRequest,
        McpToolNames.PipelineLandExecute,
        McpToolNames.PipelineLandRevert, McpToolNames.KnowledgePropose,
        McpToolNames.KnowledgeWithdraw, "run_command", "service_start", GitCommit, GitIntegrate, GitPublish, GitPullRequest
    });

    /// <summary>Shared maximum set of role-scoped Beacon MCP tools; local execution tools have separate guards.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> PipelineRoleTools { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Planner"] = PlannerTools,
                ["Actor"] = Array.AsReadOnly(ReadOnlyWorkspaceTools.Concat(ActorWorkspaceTools)
                    .Concat(ActorTerminalTools).Concat(ActorReadOnlyAlwaysTools)
                    .Distinct(StringComparer.Ordinal).ToArray()),
                ["Review"] = ReviewerTools
            });

    public static IReadOnlyList<string> ToolsForRole(string? role) =>
        role is not null && PipelineRoleTools.TryGetValue(role, out var tools)
            ? tools
            : Array.Empty<string>();
}
