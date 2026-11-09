namespace ProjectBeacon.API.Tests;

using Microsoft.AspNetCore.Http;
using ProjectBeacon.API.Auth;
using ProjectBeacon.Domain.Enums;

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
