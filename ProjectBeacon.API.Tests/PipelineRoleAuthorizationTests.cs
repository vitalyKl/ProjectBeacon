namespace ProjectBeacon.API.Tests;

using Microsoft.AspNetCore.Http;
using ProjectBeacon.API.Auth;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Domain.Common;

public sealed class PipelineRoleAuthorizationTests
{
    [Fact]
    public void PlannerCannotStartOrLaunchActorSessionsThroughApi()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var subtaskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Planner, projectId, taskId, null,
            HttpMethods.Post, $"/v1/tasks/{taskId:D}/subtasks/{subtaskId:D}/session"));

        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Planner, projectId, taskId, null,
            HttpMethods.Post, $"/v1/sessions/{sessionId:D}/launch",
            launchSessionTaskId: taskId, launchSessionRole: PipelineRole.Actor,
            launchSessionStatus: SessionStatus.Ready));
    }

    [Fact]
    public void ActorCanReadProjectKnowledgeOnlyWithMatchingToolGrants()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();
        var constraintId = Guid.NewGuid();

        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, null,
            HttpMethods.Get, $"/v1/projects/{projectId:D}/context/nodes",
            allowedMcpTools: [McpToolNames.ListContextNodes]));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, null,
            HttpMethods.Get, $"/v1/projects/{projectId:D}/context/nodes/{nodeId:D}",
            allowedMcpTools: [McpToolNames.GetContextNode]));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, null,
            HttpMethods.Get, $"/v1/projects/{projectId:D}/constraints",
            allowedMcpTools: [McpToolNames.ListConstraints]));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, null,
            HttpMethods.Get, $"/v1/projects/{projectId:D}/decisions",
            allowedMcpTools: [McpToolNames.ListDecisions]));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, null,
            HttpMethods.Get, $"/v1/projects/{projectId:D}/decisions/{decisionId:D}",
            allowedMcpTools: [McpToolNames.GetDecision]));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, null,
            HttpMethods.Get, $"/v1/projects/{projectId:D}/constraints/{constraintId:D}",
            allowedMcpTools: [McpToolNames.GetConstraint]));

        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, null,
            HttpMethods.Get, $"/v1/projects/{projectId:D}/context/nodes"));
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, null,
            HttpMethods.Get, $"/v1/projects/{projectId:D}/decisions/{decisionId:D}"));
    }

    [Fact]
    public void ActorCanReadTaskAttachmentsOnlyWithMatchingToolGrants()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var listPath = $"/v1/projects/{projectId:D}/tasks/{taskId:D}/attachments";
        var readPath = listPath + $"/{attachmentId:D}";

        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, Guid.NewGuid(),
            HttpMethods.Get, listPath, allowedMcpTools: [McpToolNames.TaskListAttachments]));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, Guid.NewGuid(),
            HttpMethods.Get, readPath, allowedMcpTools: [McpToolNames.TaskReadAttachment]));
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, Guid.NewGuid(),
            HttpMethods.Get, listPath, allowedMcpTools: [McpToolNames.TaskReadAttachment]));
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, Guid.NewGuid(),
            HttpMethods.Get, readPath, allowedMcpTools: [McpToolNames.TaskListAttachments]));
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, Guid.NewGuid(),
            HttpMethods.Get, readPath));
    }

    [Fact]
    public void ActorKnowledgeWritesAreProposalsOnlyAndRequireExplicitGrants()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var subtaskId = Guid.NewGuid();

        // Legacy Proposed/NeedsReview writes are reserved for the knowledge-curator
        // profile; an explicit MCP grant alone must not preserve stale grants on
        // other profiles such as docs-writer.
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/context/drafts",
            allowedMcpTools: [McpToolNames.ContextCreateDraft]));
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/constraints/propose",
            allowedMcpTools: [McpToolNames.ConstraintPropose],
            actorProfileKey: "docs-writer"));
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/decisions/propose",
            allowedMcpTools: [McpToolNames.DecisionPropose],
            actorProfileKey: "docs-writer"));

        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/context/drafts",
            allowedMcpTools: [McpToolNames.ContextCreateDraft],
            actorProfileKey: "knowledge-curator"));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/constraints/propose",
            allowedMcpTools: [McpToolNames.ConstraintPropose],
            actorProfileKey: "knowledge-curator"));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/decisions/propose",
            allowedMcpTools: [McpToolNames.DecisionPropose],
            actorProfileKey: "knowledge-curator"));

        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/constraints/propose"));
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/decisions/{Guid.NewGuid():D}/accept",
            allowedMcpTools: [McpToolNames.DecisionPropose, McpToolNames.AcceptDecision]));
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/constraints/{Guid.NewGuid():D}/activate",
            allowedMcpTools: [McpToolNames.ConstraintPropose, McpToolNames.ActivateConstraint]));
    }

    [Fact]
    public void ActorProposalRoutesRequireTheMatchingKnowledgeToolGrant()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var subtaskId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var createPath = $"/v1/projects/{projectId:D}/proposals";
        var minePath = createPath + "/mine";
        var withdrawPath = createPath + $"/{proposalId:D}/withdraw";
        var acceptPath = createPath + $"/{proposalId:D}/accept";

        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, createPath, allowedMcpTools: [McpToolNames.KnowledgePropose]));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Get, minePath, allowedMcpTools: [McpToolNames.KnowledgeProposals]));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, withdrawPath, allowedMcpTools: [McpToolNames.KnowledgeWithdraw]));

        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, createPath));
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Get, minePath));
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, withdrawPath));

        // Proposal tools never grant human decision operations, nor Planner/Review access.
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, acceptPath,
            allowedMcpTools: [McpToolNames.KnowledgePropose, McpToolNames.KnowledgeWithdraw]));
        foreach (var role in new[] { PipelineRole.Planner, PipelineRole.Review })
        {
            Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
                WorkstationCommandKind.RunPipelineSession, role, projectId, taskId, null,
                HttpMethods.Post, createPath,
                allowedMcpTools: [McpToolNames.KnowledgePropose]));
            Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
                WorkstationCommandKind.RunPipelineSession, role, projectId, taskId, null,
                HttpMethods.Get, minePath,
                allowedMcpTools: [McpToolNames.KnowledgeProposals]));
            Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
                WorkstationCommandKind.RunPipelineSession, role, projectId, taskId, null,
                HttpMethods.Post, withdrawPath,
                allowedMcpTools: [McpToolNames.KnowledgeWithdraw]));
        }
    }

    [Fact]
    public void PlannerCanStillCreateSubtasksAndStartReview()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Planner, projectId, taskId, null,
            HttpMethods.Post, $"/v1/tasks/{taskId:D}/subtasks"));

        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Planner, projectId, taskId, null,
            HttpMethods.Post, $"/v1/tasks/{taskId:D}/pipeline/review/start"));
    }
}
