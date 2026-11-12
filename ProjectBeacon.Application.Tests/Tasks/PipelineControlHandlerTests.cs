namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Tasks;
using Domain.Entities.Evals;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task CancelPipeline_ClosesSessions_CancelsCommands_AndReturnsActiveSubtasksToPending()
    {
        var (projectId, task) = await SeedTaskAsync("Cancel full pipeline");
        var backendId = await SeedNativeRuntimeAsync(projectId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner(backendId);

        var planner = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);
        var plannerLaunch = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(planner.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(plannerLaunch.Success, plannerLaunch.Error);

        var subtask = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "implement"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(subtask.Success, subtask.Error);
        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, subtask.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(actor.Success, actor.Error);
        var actorLaunch = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(actor.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(actorLaunch.Success, actorLaunch.Error);

        var cancelled = await new CancelPipelineHandler(factory)
            .HandleAsync(new CancelPipelineCommand(new CancelPipelineRequest(task.Id), PipelineTestActor.AdminFor(projectId)), TestContext.Current.CancellationToken);

        Assert.True(cancelled.Success, cancelled.Error);
        Assert.Equal(TaskPipelineStage.Cancelled, cancelled.Value!.Stage);
        Assert.Equal(TaskItemStatus.Todo, cancelled.Value.Status);
        Assert.Equal(SubtaskStatus.Pending, cancelled.Value.Subtasks.Single().Status);
        Assert.All(cancelled.Value.Sessions, session => Assert.Equal(SessionStatus.Closed, session.Status));

        using (TenantScope.EnterUnscoped())
        {
            var commands = await _db.WorkstationCommands
                .Where(command => command.ProjectId == projectId
                    && command.Kind == WorkstationCommandKind.RunPipelineSession)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, commands.Count);
            Assert.All(commands, command => Assert.Equal(WorkstationCommandStatus.Cancelled, command.Status));
        }
    }

    [Fact]
    public async Task CancelSubtask_ReturnsActorWorkToPending_AndCancelsItsCommand()
    {
        var (projectId, task) = await SeedTaskAsync("Cancel subtask");
        var backendId = await SeedNativeRuntimeAsync(projectId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner(backendId);

        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        var subtask = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "implement"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(subtask.Success, subtask.Error);
        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, subtask.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(actor.Success, actor.Error);
        var launch = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(actor.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(launch.Success, launch.Error);

        var cancelled = await new CancelSubtaskHandler(factory)
            .HandleAsync(new CancelSubtaskCommand(new CancelSubtaskRequest(task.Id, subtask.Value.Id), PipelineTestActor.AdminFor(projectId)), TestContext.Current.CancellationToken);

        Assert.True(cancelled.Success, cancelled.Error);
        Assert.Equal(SubtaskStatus.Pending, cancelled.Value!.Subtasks.Single().Status);
        Assert.Equal(SessionStatus.Closed, cancelled.Value.Sessions.Single(s => s.Id == actor.Value.Id).Status);
        using (TenantScope.EnterUnscoped())
        {
            var command = await _db.WorkstationCommands.SingleAsync(
                row => row.Kind == WorkstationCommandKind.RunPipelineSession,
                TestContext.Current.CancellationToken);
            Assert.Equal(WorkstationCommandStatus.Cancelled, command.Status);
        }
    }

    [Fact]
    public async Task RestartPipeline_RemovesSubtasksAndReviewResults_ButKeepsClosedSessionHistory()
    {
        var (projectId, task) = await SeedTaskAsync("Restart pipeline");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var planner = await new StartPipelineHandler(factory, new FakeSpawner())
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);

        var subtask = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "old plan"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(subtask.Success, subtask.Error);
        using (TenantScope.EnterUnscoped())
        {
            _db.ReviewVerdicts.Add(ReviewVerdict.Create(task.Id, projectId, ReviewVerdictKind.Approve, "old approval"));
            _db.ReviewRuns.Add(ReviewRun.Start(projectId, task.Id, ReviewerType.Agent, Guid.NewGuid()));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var restarted = await new RestartPipelineHandler(factory)
            .HandleAsync(new RestartPipelineCommand(new RestartPipelineRequest(task.Id), PipelineTestActor.AdminFor(projectId)), TestContext.Current.CancellationToken);

        Assert.True(restarted.Success, restarted.Error);
        Assert.Null(restarted.Value!.Stage);
        Assert.Equal(TaskItemStatus.Todo, restarted.Value.Status);
        Assert.Empty(restarted.Value.Subtasks);
        Assert.Empty(restarted.Value.Verdicts);
        Assert.Single(restarted.Value.Sessions);
        Assert.Equal(SessionStatus.Closed, restarted.Value.Sessions.Single().Status);
        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(0, await _db.ReviewRuns.CountAsync(r => r.TaskId == task.Id, TestContext.Current.CancellationToken));
            Assert.Equal(0, await _db.Subtasks.CountAsync(s => s.TaskId == task.Id, TestContext.Current.CancellationToken));
            Assert.Equal(0, await _db.ReviewVerdicts.CountAsync(v => v.TaskId == task.Id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task DeleteTask_RemovesTaskEvenWhenPipelineWasNotClosed_AndCancelsAgentWork()
    {
        var (projectId, task) = await SeedTaskAsync("Delete active task");
        var backendId = await SeedNativeRuntimeAsync(projectId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner(backendId);
        using (TenantScope.EnterUnscoped())
        {
            _db.TaskSteps.Add(TaskStep.Create(task.Id, projectId, "Remove with task", 0));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var planner = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);
        var launch = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(planner.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(launch.Success, launch.Error);

        var actor = new ActorContext(ActorType.Human, Guid.NewGuid(), true, null, null, null, null, ApiTokenCapability.None);
        var deleted = await new DeleteTaskHandler(factory, new AuthorizationService())
            .HandleAsync(new DeleteTaskCommand(new DeleteTaskRequest(task.Id), actor), TestContext.Current.CancellationToken);

        Assert.True(deleted.Success, deleted.Error);
        using (TenantScope.EnterUnscoped())
        {
            Assert.False(await _db.Tasks.AnyAsync(t => t.Id == task.Id, TestContext.Current.CancellationToken));
            Assert.False(await _db.TaskSteps.AnyAsync(step => step.TaskId == task.Id, TestContext.Current.CancellationToken));
            var command = await _db.WorkstationCommands.SingleAsync(
                row => row.Kind == WorkstationCommandKind.RunPipelineSession,
                TestContext.Current.CancellationToken);
            Assert.Equal(WorkstationCommandStatus.Cancelled, command.Status);
        }
    }
}
