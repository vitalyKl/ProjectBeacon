namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task InitialPlannerNoOp_RecordsHumanAttentionWhileTaskIsStillPlanning()
    {
        var (projectId, task) = await SeedTaskAsync("Initial Planner no-op");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner();
        var planner = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);

        using (TenantScope.EnterUnscoped())
        {
            var session = await _db.PipelineSessions
                .SingleAsync(row => row.Id == planner.Value!.Id, TestContext.Current.CancellationToken);
            session.Close();
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await new PipelineActorQueueDispatcher(factory, spawner)
            .DispatchNextAsync(task.Id, planner.Value!.Id, TestContext.Current.CancellationToken);

        using (TenantScope.EnterUnscoped())
        {
            var attention = await _db.TaskWorkspaceSections.AsNoTracking()
                .SingleAsync(section => section.TaskId == task.Id && section.Key == "system/planner-status",
                    TestContext.Current.CancellationToken);
            Assert.Contains("Human input is required", attention.Content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EmptyActorQueue_WakesPlannerExactlyOnce()
    {
        var (factory, spawner, task, actorSessionId, _) =
            await CreateCompletedActorAsync("Wake Planner after the last Actor", wakePlannerAfter: false);

        var dispatcher = new PipelineActorQueueDispatcher(factory, spawner);
        await dispatcher.DispatchNextAsync(task.Id, actorSessionId, TestContext.Current.CancellationToken);
        await dispatcher.DispatchNextAsync(task.Id, actorSessionId, TestContext.Current.CancellationToken);

        using (TenantScope.EnterUnscoped())
        {
            _db.ChangeTracker.Clear();
            var planners = await _db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(session => session.TaskId == task.Id && session.Role == PipelineRole.Planner)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, planners.Count);
            Assert.Single(planners, session => session.Status is SessionStatus.Active or SessionStatus.Ready);
        }
    }

    [Fact]
    public async Task ExplicitActorCheckpoint_WakesPlannerBeforeStartingPendingActor()
    {
        var (factory, spawner, task, actorSessionId, pendingSubtaskId) =
            await CreateCompletedActorAsync("Wake Planner at the explorer checkpoint", wakePlannerAfter: true,
                createPendingSecondSubtask: true);

        var dispatcher = new PipelineActorQueueDispatcher(factory, spawner);
        await dispatcher.DispatchNextAsync(task.Id, actorSessionId, TestContext.Current.CancellationToken);

        using (TenantScope.EnterUnscoped())
        {
            _db.ChangeTracker.Clear();
            var pending = await _db.Subtasks.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(subtask => subtask.Id == pendingSubtaskId, TestContext.Current.CancellationToken);
            var completedSubtaskId = await _db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(session => session.Id == actorSessionId)
                .Select(session => session.SubtaskId!.Value)
                .SingleAsync(TestContext.Current.CancellationToken);
            var checkpoint = await _db.Subtasks.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(subtask => subtask.Id == completedSubtaskId,
                    TestContext.Current.CancellationToken);
            var actors = await _db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(session => session.TaskId == task.Id && session.Role == PipelineRole.Actor,
                    TestContext.Current.CancellationToken);
            var planners = await _db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(session => session.TaskId == task.Id && session.Role == PipelineRole.Planner,
                    TestContext.Current.CancellationToken);

            Assert.Equal(SubtaskStatus.Pending, pending.Status);
            Assert.False(checkpoint.WakePlannerAfter);
            Assert.Equal(1, actors);
            Assert.Equal(2, planners);
        }
    }

    private async Task<(BeaconDbFactory Factory, FakeSpawner Spawner, TaskItem Task, Guid ActorSessionId, Guid PendingSubtaskId)>
        CreateCompletedActorAsync(string title, bool wakePlannerAfter, bool createPendingSecondSubtask = false)
    {
        var (projectId, task) = await SeedTaskAsync(title);
        var backendId = await SeedNativeRuntimeAsync(projectId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner(backendId);

        var planner = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);

        using (TenantScope.EnterUnscoped())
        {
            _db.ChangeTracker.Clear();
            var initialPlanner = await _db.PipelineSessions.IgnoreQueryFilters()
                .SingleAsync(session => session.Id == planner.Value!.Id, TestContext.Current.CancellationToken);
            initialPlanner.Close();
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var first = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
                task.Id, "Explore the relevant implementation.",
                WakePlannerAfter: wakePlannerAfter)),
                TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        Guid? pendingId = null;
        if (createPendingSecondSubtask)
        {
            var pending = await new CreateSubtaskHandler(factory)
                .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
                    task.Id, "Implement the next batch after the checkpoint.")),
                    TestContext.Current.CancellationToken);
            Assert.True(pending.Success, pending.Error);
            pendingId = pending.Value!.Id;
        }

        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, first.Value!.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(actor.Success, actor.Error);

        var report = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(
                task.Id, first.Value.Id, string.Empty, "Exploration report: implementation boundaries identified.")),
                TestContext.Current.CancellationToken);
        Assert.True(report.Success, report.Error);

        return (factory, spawner, task, actor.Value!.Id, pendingId ?? Guid.Empty);
    }
}
