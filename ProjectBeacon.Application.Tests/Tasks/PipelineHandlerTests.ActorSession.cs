namespace ProjectBeacon.Application.Tests;

using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task StartActorSession_SessionInsertFailure_RollsBackSubtaskAndStageClaims()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, new FakeSpawner())
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);

        using (TenantScope.EnterUnscoped())
        {
            await _db.Tasks
                .Where(t => t.Id == task.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.PipelineStage, TaskPipelineStage.ReopenedForRevision), TestContext.Current.CancellationToken);
        }

        var resultHandler = new StartActorSessionHandler(factory, new SessionInsertFailingSpawner());
        await Assert.ThrowsAsync<DbUpdateException>(() => resultHandler.HandleAsync(
            new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)),
            TestContext.Current.CancellationToken));

        using (TenantScope.EnterUnscoped())
        {
            var stage = await _db.Tasks.AsNoTracking()
                .Where(t => t.Id == task.Id)
                .Select(t => t.PipelineStage)
                .FirstAsync(TestContext.Current.CancellationToken);
            var status = await _db.Subtasks.AsNoTracking()
                .Where(s => s.Id == sub.Value.Id)
                .Select(s => s.Status)
                .FirstAsync(TestContext.Current.CancellationToken);

            Assert.Equal(TaskPipelineStage.ReopenedForRevision, stage);
            Assert.Equal(SubtaskStatus.Pending, status);
            Assert.Equal(0, await _db.PipelineSessions.CountAsync(
                s => s.TaskId == task.Id && s.Role == PipelineRole.Actor,
                TestContext.Current.CancellationToken));
        }
    }

    private sealed class SessionInsertFailingSpawner : ISessionSpawner
    {
        public async Task<SpawnedSession> SpawnAsync(
            IBeaconDb db,
            Guid projectId,
            PipelineRole role,
            Guid taskId,
            CancellationToken ct)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER fail_actor_session_insert
                BEFORE INSERT ON PipelineSessions
                BEGIN
                    SELECT RAISE(FAIL, 'simulated actor session persistence failure');
                END;
                """,
                ct);
            return new SpawnedSession(null, null, null);
        }
    }

    [Fact]
    public async Task StartActorSession_CrashWindow_NoOpenSession_DeterministicFailure_NoSpawn()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);

        // Simulate a crash between the claim commit and the session insert:
        // the subtask is InProgress, but no actor session was persisted.
        using (TenantScope.EnterUnscoped())
        {
            var updated = await _db.Subtasks
                .Where(s => s.Id == sub.Value!.Id && s.Status == SubtaskStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(s => s.Status, SubtaskStatus.InProgress), TestContext.Current.CancellationToken);
            Assert.Equal(1, updated);
        }

        var result = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value.Id)), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("Subtask is in progress without an open session. Fail and reopen it to retry.", result.Error);
        // StartPipeline already spawned a Planner session; this start must not spawn an Actor one.
        Assert.DoesNotContain(spawner.Calls, c => c.Role == PipelineRole.Actor);

        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(0, await _db.PipelineSessions.CountAsync(s => s.TaskId == task.Id && s.Role == PipelineRole.Actor, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task StartActorSession_WithOpenSession_ReturnsSameSession_WithoutRespawn()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);

        var first = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        // A second start for the same subtask returns the open session instead of spawning.
        var again = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value.Id)), TestContext.Current.CancellationToken);
        Assert.True(again.Success, again.Error);
        Assert.Equal(first.Value!.Id, again.Value!.Id);
        // One Actor spawn total (the Planner spawn came from StartPipeline).
        Assert.Single(spawner.Calls, c => c.Role == PipelineRole.Actor);

        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(1, await _db.PipelineSessions.CountAsync(s => s.TaskId == task.Id && s.Role == PipelineRole.Actor, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task StuckSubtask_FailAndReopen_RestartsThroughReviewVerdict()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);

        using (TenantScope.EnterUnscoped())
        {
            var updated = await _db.Subtasks
                .Where(s => s.Id == sub.Value!.Id && s.Status == SubtaskStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(s => s.Status, SubtaskStatus.InProgress), TestContext.Current.CancellationToken);
            Assert.Equal(1, updated);
        }

        // The stuck subtask fails deterministically on start; no spawn, no session.
        var stuck = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value.Id)), TestContext.Current.CancellationToken);
        Assert.False(stuck.Success);
        Assert.DoesNotContain(spawner.Calls, c => c.Role == PipelineRole.Actor);

        // Operator recovery, no auto-retry: fail the subtask, run review, record a
        // ReopenSubtask verdict, then start the actor session again.
        var failed = await new FailSubtaskHandler(factory)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(task.Id, sub.Value.Id, "stuck after crash")), TestContext.Current.CancellationToken);
        Assert.True(failed.Success, failed.Error);
        Assert.Equal(SubtaskStatus.Failed, failed.Value!.Subtasks.Single(s => s.Id == sub.Value.Id).Status);

        using (new EnvGuard(ReopenCyclesEnv, null))
        {
            var review = await new StartReviewHandler(factory, spawner)
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)), TestContext.Current.CancellationToken);
            Assert.True(review.Success, review.Error);

            var reopened = await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.ReopenSubtask, "retry after crash", sub.Value.Id)), TestContext.Current.CancellationToken);
            Assert.True(reopened.Success, reopened.Error);
            Assert.Equal(TaskPipelineStage.ReopenedForRevision, reopened.Value!.Stage);
            var subtask = reopened.Value.Subtasks.Single(s => s.Id == sub.Value.Id);
            Assert.Equal(SubtaskStatus.Pending, subtask.Status);
            Assert.Equal(1, subtask.ReopenCount);
            Assert.Contains("retry after crash", subtask.Instructions);

            var actor = await new StartActorSessionHandler(factory, spawner)
                .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value.Id)), TestContext.Current.CancellationToken);
            Assert.True(actor.Success, actor.Error);
            var after = await new GetPipelineHandler(factory)
                .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
            Assert.Equal(TaskPipelineStage.Executing, after.Value!.Stage);
            Assert.Equal(SubtaskStatus.InProgress,
                after.Value.Subtasks.Single(s => s.Id == sub.Value.Id).Status);
        }

        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(1, await _db.PipelineSessions.CountAsync(s => s.TaskId == task.Id && s.Role == PipelineRole.Actor, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task FailSubtask_DoneSubtask_Rejects_WithoutOverwriting()
    {
        var done = await SeedDoneSubtaskAsync();
        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));

        var result = await new FailSubtaskHandler(factory)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(done.Task.Id, done.SubtaskId, "try to fail done")), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("Subtask is already done; reopen it through a review verdict.", result.Error);

        using (TenantScope.EnterUnscoped())
        {
            var row = await _db.Subtasks.AsNoTracking().SingleAsync(s => s.Id == done.SubtaskId, TestContext.Current.CancellationToken);
            Assert.Equal(SubtaskStatus.Done, row.Status);
            Assert.Equal("ok", row.Summary); // untouched
        }
    }

    [Fact]
    public async Task FailSubtask_FailedSubtask_Idempotent_NoSecondWrite()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work")), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);
        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id)), TestContext.Current.CancellationToken);
        Assert.True(actor.Success, actor.Error);

        var first = await new FailSubtaskHandler(factory)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(task.Id, sub.Value.Id, "attempt 1 failed")), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);
        var firstSub = first.Value!.Subtasks.Single(s => s.Id == sub.Value.Id);
        Assert.Equal(SubtaskStatus.Failed, firstSub.Status);

        // Repeating the failure is a no-op: same status, the first summary is kept.
        var again = await new FailSubtaskHandler(factory)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(task.Id, sub.Value.Id, "duplicate report")), TestContext.Current.CancellationToken);
        Assert.True(again.Success, again.Error);
        var againSub = again.Value!.Subtasks.Single(s => s.Id == sub.Value.Id);
        Assert.Equal(SubtaskStatus.Failed, againSub.Status);
        Assert.Equal(firstSub.Summary, againSub.Summary);
    }
}
