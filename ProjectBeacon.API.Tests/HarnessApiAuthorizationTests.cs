namespace ProjectBeacon.API.Tests;

using Domain.Enums;
using Microsoft.AspNetCore.Http;
using ProjectBeacon.API.Auth;

public sealed class HarnessApiAuthorizationTests
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _taskId = Guid.NewGuid();
    private readonly Guid _subtaskId = Guid.NewGuid();

    [Fact]
    public void ActorToken_IsBoundToItsOwnSubtaskLifecycleRoutes()
    {
        var resultPath = $"/v1/tasks/{_taskId:D}/subtasks/{_subtaskId:D}/result";
        var failPath = $"/v1/tasks/{_taskId:D}/subtasks/{_subtaskId:D}/fail";

        Assert.True(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor,
            HttpMethods.Post, resultPath, _taskId, _subtaskId));
        Assert.True(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor,
            HttpMethods.Post, failPath, _taskId, _subtaskId));

        Assert.False(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/subtasks/{Guid.NewGuid():D}/result", _taskId, _subtaskId));
        Assert.False(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor,
            HttpMethods.Post, $"/v1/tasks/{Guid.NewGuid():D}/subtasks/{_subtaskId:D}/result", _taskId, _subtaskId));
        Assert.False(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/pipeline/approve", _taskId, _subtaskId));
        Assert.False(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Actor,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/pipeline/verdict", _taskId, _subtaskId));
    }

    [Fact]
    public void PlannerToken_CanOrchestrateOnlyItsPinnedTask()
    {
        Assert.True(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Planner,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/subtasks", _taskId));
        Assert.True(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Planner,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/subtasks/{_subtaskId:D}/session", _taskId));
        Assert.True(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Planner,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/pipeline/review/start", _taskId));

        Assert.False(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Planner,
            HttpMethods.Post, $"/v1/tasks/{Guid.NewGuid():D}/subtasks", _taskId));
        Assert.False(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Planner,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/pipeline/approve", _taskId));
        Assert.False(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Planner,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/subtasks/{_subtaskId:D}/result", _taskId, _subtaskId));
    }

    [Fact]
    public void PlannerCanLaunchOnlyReadyActorSessionFromItsOwnTask()
    {
        var sessionId = Guid.NewGuid();
        var path = $"/v1/sessions/{sessionId:D}/launch";

        Assert.True(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession,
            PipelineRole.Planner,
            _projectId,
            _taskId,
            null,
            HttpMethods.Post,
            path,
            _taskId,
            PipelineRole.Actor,
            SessionStatus.Ready));

        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession,
            PipelineRole.Planner,
            _projectId,
            _taskId,
            null,
            HttpMethods.Post,
            path,
            Guid.NewGuid(),
            PipelineRole.Actor,
            SessionStatus.Ready));

        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession,
            PipelineRole.Planner,
            _projectId,
            _taskId,
            null,
            HttpMethods.Post,
            path,
            _taskId,
            PipelineRole.Review,
            SessionStatus.Ready));

        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunPipelineSession,
            PipelineRole.Planner,
            _projectId,
            _taskId,
            null,
            HttpMethods.Post,
            path,
            _taskId,
            PipelineRole.Actor,
            SessionStatus.Active));
    }

    [Fact]
    public void ReviewerToken_CanReviewButCannotPlanOrExecuteActorWork()
    {
        Assert.True(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Review,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/pipeline/verdict", _taskId));
        Assert.True(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Review,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/pipeline/approve", _taskId));

        Assert.False(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Review,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/subtasks", _taskId));
        Assert.False(Allowed(WorkstationCommandKind.RunPipelineSession, PipelineRole.Review,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/subtasks/{_subtaskId:D}/result", _taskId, _subtaskId));
    }

    [Fact]
    public void EvalToken_IsReadOnlyAndCannotAccessPipelineLifecycle()
    {
        Assert.True(Allowed(WorkstationCommandKind.RunEvalTurn, null,
            HttpMethods.Get, $"/v1/projects/{_projectId:D}/tasks", _taskId));
        Assert.True(Allowed(WorkstationCommandKind.RunEvalTurn, null,
            HttpMethods.Get, $"/v1/projects/{_projectId:D}/context/nodes", _taskId));

        Assert.False(Allowed(WorkstationCommandKind.RunEvalTurn, null,
            HttpMethods.Post, $"/v1/projects/{_projectId:D}/tasks", _taskId));
        Assert.False(Allowed(WorkstationCommandKind.RunEvalTurn, null,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/pipeline/start", _taskId));
        Assert.False(Allowed(WorkstationCommandKind.RunEvalTurn, null,
            HttpMethods.Post, $"/v1/tasks/{_taskId:D}/subtasks", _taskId));
    }

    [Fact]
    public void PlannerContextCompileMustStayPinnedToItsPipelineTask()
    {
        Assert.True(ApiTokenAuthMiddleware.IsContextCompileBodyScoped(
            $"""{"taskId":"{{_taskId:D}}","includeHandoff":false}""", _taskId));
        Assert.False(ApiTokenAuthMiddleware.IsContextCompileBodyScoped(
            $"""{"taskId":"{{Guid.NewGuid():D}}"}""", _taskId));
        Assert.False(ApiTokenAuthMiddleware.IsContextCompileBodyScoped(
            """{"includeHandoff":false}""", _taskId));
    }

    [Fact]
    public void AllHarnessRequestsAreBoundToTheTokenProject()
    {
        Assert.False(ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            WorkstationCommandKind.RunEvalTurn,
            null,
            _projectId,
            _taskId,
            null,
            HttpMethods.Get,
            $"/v1/projects/{Guid.NewGuid():D}/tasks"));
    }

    private bool Allowed(
        WorkstationCommandKind commandKind,
        PipelineRole? role,
        string method,
        string path,
        Guid? taskId = null,
        Guid? subtaskId = null)
        => ApiTokenAuthMiddleware.IsHarnessRequestAllowed(
            commandKind,
            role,
            _projectId,
            taskId,
            subtaskId,
            method,
            path);
}
