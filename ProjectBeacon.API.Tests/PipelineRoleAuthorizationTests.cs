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

        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, null,
            HttpMethods.Get, $"/v1/projects/{projectId:D}/context/nodes"));
    }

    [Fact]
    public void ActorKnowledgeWritesAreProposalsOnlyAndRequireExplicitGrants()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var subtaskId = Guid.NewGuid();

        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/context/drafts",
            allowedMcpTools: [McpToolNames.ContextCreateDraft]));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/constraints/propose",
            allowedMcpTools: [McpToolNames.ConstraintPropose]));
        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor, projectId, taskId, subtaskId,
            HttpMethods.Post, $"/v1/projects/{projectId:D}/decisions/propose",
            allowedMcpTools: [McpToolNames.DecisionPropose]));

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
