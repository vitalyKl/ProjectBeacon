namespace ProjectBeacon.Application.Tests;

using Application.Persistence;
using Application.Authorization;
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
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);

        using (TenantScope.EnterUnscoped())
        {
            await _db.Tasks
                .Where(t => t.Id == task.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.PipelineStage, TaskPipelineStage.ReopenedForRevision), TestContext.Current.CancellationToken);
        }

        var resultHandler = new StartActorSessionHandler(factory, new SessionInsertFailingSpawner());
        await Assert.ThrowsAsync<DbUpdateException>(() => resultHandler.HandleAsync(
            new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)),
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
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
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
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("Subtask is in progress without an open Actor session. Fail and reopen it to retry.", result.Error);
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
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);

        var first = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        // A second start for the same subtask returns the open session instead of spawning.
        var again = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
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
    public async Task StartActorSession_WaitingForUser_ReturnsExistingSessionWithoutRespawn()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);

        var first = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);

        using (TenantScope.EnterUnscoped())
        {
            await _db.Subtasks
                .Where(s => s.Id == sub.Value.Id && s.Status == SubtaskStatus.InProgress)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SubtaskStatus.WaitingForUser), TestContext.Current.CancellationToken);
            await _db.PipelineSessions
                .Where(s => s.Id == first.Value!.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.WaitingForUser), TestContext.Current.CancellationToken);
        }

        var again = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);

        Assert.True(again.Success, again.Error);
        Assert.Equal(first.Value!.Id, again.Value!.Id);
        Assert.Single(spawner.Calls, call => call.Role == PipelineRole.Actor);
        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(SubtaskStatus.WaitingForUser,
                await _db.Subtasks.Where(s => s.Id == sub.Value.Id).Select(s => s.Status).SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(SessionStatus.WaitingForUser,
                await _db.PipelineSessions.Where(s => s.Id == first.Value.Id).Select(s => s.Status).SingleAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task FailSubtask_PendingWithoutActorSession_AdvancesToNextQueuedSubtask()
    {
        var (projectId, task) = await SeedTaskAsync("Fail a pending subtask");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var planner = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);

        var failedSubtask = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "This work is no longer needed."), PipelineTestActor.AdminFor(task.ProjectId)),
                TestContext.Current.CancellationToken);
        var nextSubtask = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "Continue with the next item."), PipelineTestActor.AdminFor(task.ProjectId)),
                TestContext.Current.CancellationToken);
        Assert.True(failedSubtask.Success, failedSubtask.Error);
        Assert.True(nextSubtask.Success, nextSubtask.Error);

        using (TenantScope.EnterUnscoped())
        {
            var plannerRow = await _db.PipelineSessions
                .SingleAsync(row => row.Id == planner.Value!.Id, TestContext.Current.CancellationToken);
            plannerRow.Close();
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var queue = new PipelineActorQueueDispatcher(factory, spawner);
        var failed = await new FailSubtaskHandler(factory, queue)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(
                task.Id, failedSubtask.Value!.Id, "The pending work was superseded."), PipelineTestActor.AdminFor(task.ProjectId)),
                TestContext.Current.CancellationToken);

        Assert.True(failed.Success, failed.Error);
        using (TenantScope.EnterUnscoped())
        {
            var first = await _db.Subtasks.AsNoTracking()
                .SingleAsync(row => row.Id == failedSubtask.Value.Id, TestContext.Current.CancellationToken);
            var next = await _db.Subtasks.AsNoTracking()
                .SingleAsync(row => row.Id == nextSubtask.Value!.Id, TestContext.Current.CancellationToken);
            Assert.Equal(SubtaskStatus.Failed, first.Status);
            Assert.Equal(SubtaskStatus.InProgress, next.Status);

            var actor = await _db.PipelineSessions.AsNoTracking()
                .SingleAsync(row => row.TaskId == task.Id && row.Role == PipelineRole.Actor,
                    TestContext.Current.CancellationToken);
            Assert.Equal(nextSubtask.Value.Id, actor.SubtaskId);
            Assert.Equal(SessionStatus.Ready, actor.Status);
        }
    }

    [Fact]
    public async Task FailSubtask_WhileWaitingForUser_ClosesActorAndCancelsWorkstationCommand()
    {
        var (projectId, task) = await SeedTaskAsync("Cancel waiting Actor");
        var backendId = await SeedNativeRuntimeAsync(projectId);
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var plannerSpawner = new FakeSpawner(backendId);
        var initialPlanner = await new StartPipelineHandler(factory, plannerSpawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(initialPlanner.Success, initialPlanner.Error);

        using (TenantScope.EnterUnscoped())
        {
            var plannerSession = await _db.PipelineSessions
                .SingleAsync(row => row.Id == initialPlanner.Value!.Id, TestContext.Current.CancellationToken);
            plannerSession.Close();
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await _db.Tasks
                .Where(row => row.Id == task.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.PipelineStage, TaskPipelineStage.Executing), TestContext.Current.CancellationToken);
        }

        var created = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(created.Success, created.Error);
        var next = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work on the next queued item"), PipelineTestActor.AdminFor(task.ProjectId)),
                TestContext.Current.CancellationToken);
        Assert.True(next.Success, next.Error);

        var actorSpawner = new FakeSpawner(backendId);
        var actor = await new StartActorSessionHandler(factory, actorSpawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, created.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(actor.Success, actor.Error);

        var launched = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(actor.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(launched.Success, launched.Error);

        Guid commandId;
        using (TenantScope.EnterUnscoped())
        {
            commandId = await _db.WorkstationCommands
                .Where(row => row.Kind == WorkstationCommandKind.RunPipelineSession
                    && row.ProjectId == projectId)
                .Select(row => row.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
            await _db.Subtasks
                .Where(row => row.Id == created.Value.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, SubtaskStatus.WaitingForUser), TestContext.Current.CancellationToken);
            await _db.PipelineSessions
                .Where(row => row.Id == actor.Value.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, SessionStatus.WaitingForUser), TestContext.Current.CancellationToken);
        }

        var pipelineQueue = new PipelineActorQueueDispatcher(factory, actorSpawner);
        var failed = await new FailSubtaskHandler(factory, pipelineQueue)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(task.Id, created.Value.Id, "cancel the waiting Actor"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(failed.Success, failed.Error);
        Assert.Equal(SubtaskStatus.Failed, failed.Value!.Subtasks.Single(row => row.Id == created.Value.Id).Status);

        // Retrying a failed state also retries best-effort cleanup; cancellation is idempotent.
        var retried = await new FailSubtaskHandler(factory, pipelineQueue)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(task.Id, created.Value.Id, "retry cancellation"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(retried.Success, retried.Error);

        using (TenantScope.EnterUnscoped())
        {
            var session = await _db.PipelineSessions.AsNoTracking()
                .SingleAsync(row => row.Id == actor.Value.Id, TestContext.Current.CancellationToken);
            var command = await _db.WorkstationCommands.AsNoTracking()
                .SingleAsync(row => row.Id == commandId, TestContext.Current.CancellationToken);

            Assert.Equal(SessionStatus.Closed, session.Status);
            Assert.Equal(WorkstationCommandStatus.Cancelled, command.Status);
            Assert.Null(command.LeaseExpiresAt);
            Assert.NotNull(command.CompletedAt);

            var nextSubtask = await _db.Subtasks.AsNoTracking()
                .SingleAsync(row => row.Id == next.Value!.Id, TestContext.Current.CancellationToken);
            Assert.Equal(SubtaskStatus.InProgress, nextSubtask.Status);
            Assert.True(await _db.PipelineSessions.AsNoTracking().AnyAsync(row =>
                row.TaskId == task.Id && row.Role == PipelineRole.Actor
                && row.SubtaskId == next.Value.Id
                && (row.Status == SessionStatus.Ready || row.Status == SessionStatus.Active),
                TestContext.Current.CancellationToken));

            var report = await _db.TaskWorkspaceSections.AsNoTracking()
                .SingleAsync(section => section.TaskId == task.Id
                    && section.AuthorSubtaskId == created.Value.Id
                    && section.Kind == "Report",
                    TestContext.Current.CancellationToken);
            Assert.Equal("report/" + created.Value.Id.ToString("N") + "/attempt-1", report.Key);
            Assert.Equal("Server", report.AuthorType);
            Assert.Equal("cancel the waiting Actor", report.Content);
        }
    }

    [Fact]
    public async Task StuckSubtask_FailAndReopen_RestartsThroughReviewVerdict()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var spawner = new FakeSpawner();
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
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
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.False(stuck.Success);
        Assert.DoesNotContain(spawner.Calls, c => c.Role == PipelineRole.Actor);

        // Operator recovery, no auto-retry: fail the subtask, run review, record a
        // ReopenSubtask verdict, then start the actor session again.
        var failed = await new FailSubtaskHandler(factory)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(task.Id, sub.Value.Id, "stuck after crash"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(failed.Success, failed.Error);
        Assert.Equal(SubtaskStatus.Failed, failed.Value!.Subtasks.Single(s => s.Id == sub.Value.Id).Status);

        using (new EnvGuard(ReopenCyclesEnv, null))
        {
            var review = await new StartReviewHandler(factory, spawner)
                .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
            Assert.True(review.Success, review.Error);

            var reopened = await new RecordReviewVerdictHandler(factory)
                .HandleAsync(new RecordReviewVerdictCommand(
                    new RecordReviewVerdictRequest(task.Id, ReviewVerdictKind.ReopenSubtask, "retry after crash", sub.Value.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
            Assert.True(reopened.Success, reopened.Error);
            Assert.Equal(TaskPipelineStage.ReopenedForRevision, reopened.Value!.Stage);
            var subtask = reopened.Value.Subtasks.Single(s => s.Id == sub.Value.Id);
            Assert.Equal(SubtaskStatus.Pending, subtask.Status);
            Assert.Equal(1, subtask.ReopenCount);
            Assert.Contains("retry after crash", subtask.Instructions);

            var actor = await new StartActorSessionHandler(factory, spawner)
                .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
            Assert.True(actor.Success, actor.Error);
            var after = await new GetPipelineHandler(factory)
                .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
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
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(done.Task.Id, done.SubtaskId, "try to fail done"), PipelineTestActor.AdminFor(done.Task.ProjectId)), TestContext.Current.CancellationToken);
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
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);
        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(actor.Success, actor.Error);

        var first = await new FailSubtaskHandler(factory)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(task.Id, sub.Value.Id, "attempt 1 failed"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.Error);
        var firstSub = first.Value!.Subtasks.Single(s => s.Id == sub.Value.Id);
        Assert.Equal(SubtaskStatus.Failed, firstSub.Status);

        // Repeating the failure is a no-op: same status, the first summary is kept.
        var again = await new FailSubtaskHandler(factory)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(task.Id, sub.Value.Id, "duplicate report"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(again.Success, again.Error);
        var againSub = again.Value!.Subtasks.Single(s => s.Id == sub.Value.Id);
        Assert.Equal(SubtaskStatus.Failed, againSub.Status);
        Assert.Equal(firstSub.Summary, againSub.Summary);

        using (TenantScope.EnterUnscoped())
        {
            var report = await _db.TaskWorkspaceSections.AsNoTracking()
                .SingleAsync(section => section.TaskId == task.Id && section.AuthorSubtaskId == sub.Value.Id,
                    TestContext.Current.CancellationToken);
            Assert.Equal("Report", report.Kind);
            Assert.Equal("Server", report.AuthorType);
            Assert.Equal("Actor", report.AuthorRole);
            Assert.Equal(actor.Value.Id, report.AuthorSessionId);
            Assert.Equal("attempt 1 failed", report.Content);
        }
    }
    [Fact]
    public async Task ReportSubtaskResult_OversizedSummary_ReturnsValidationErrorInsteadOfDatabaseFailure()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner();
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);
        var actor = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(actor.Success, actor.Error);

        var result = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(
                task.Id, sub.Value.Id, "verified-diff", new string('x', Subtask.MaxSummaryLength + 1)), PipelineTestActor.AdminFor(task.ProjectId)),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("5000-character limit", result.Error);
        using (TenantScope.EnterUnscoped())
        {
            var row = await _db.Subtasks.AsNoTracking().SingleAsync(s => s.Id == sub.Value.Id, TestContext.Current.CancellationToken);
            Assert.Equal(SubtaskStatus.InProgress, row.Status);
            Assert.Null(row.Summary);
        }
    }

    [Fact]
    public async Task FailSubtask_OversizedReason_ReturnsValidationError()
    {
        var (projectId, task) = await SeedTaskAsync("T");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner();
        await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        var sub = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "work"), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);
        Assert.True(sub.Success, sub.Error);
        await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(task.Id, sub.Value!.Id), PipelineTestActor.AdminFor(task.ProjectId)), TestContext.Current.CancellationToken);

        var result = await new FailSubtaskHandler(factory)
            .HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(
                task.Id, sub.Value.Id, new string('x', Subtask.MaxSummaryLength + 1)), PipelineTestActor.AdminFor(task.ProjectId)),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("5000-character limit", result.Error);
    }


    [Fact]
    public async Task StartActorSession_RequiresSessionDriveAtApplicationBoundary()
    {
        var (projectId, task) = await SeedTaskAsync("Actor authorization");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner();

        var planner = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(
                new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(projectId)),
                TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);

        var subtask = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(
                new CreateSubtaskRequest(task.Id, "run actor"), PipelineTestActor.AdminFor(projectId)),
                TestContext.Current.CancellationToken);
        Assert.True(subtask.Success, subtask.Error);

        var taskWriteOnly = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            projectId, null, ApiTokenCapability.TaskWrite);
        var result = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(
                new StartActorSessionRequest(task.Id, subtask.Value!.Id), taskWriteOnly),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.DoesNotContain(spawner.Calls, call => call.Role == PipelineRole.Actor);
    }

    [Fact]
    public async Task LaunchSession_RequiresSessionDriveAtApplicationBoundary()
    {
        var (projectId, task) = await SeedTaskAsync("Launch authorization");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner();

        var planner = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(
                new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(projectId)),
                TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);
        var subtask = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(
                new CreateSubtaskRequest(task.Id, "run actor"), PipelineTestActor.AdminFor(projectId)),
                TestContext.Current.CancellationToken);
        Assert.True(subtask.Success, subtask.Error);
        var session = await new StartActorSessionHandler(factory, spawner)
            .HandleAsync(new StartActorSessionCommand(
                new StartActorSessionRequest(task.Id, subtask.Value!.Id), PipelineTestActor.AdminFor(projectId)),
                TestContext.Current.CancellationToken);
        Assert.True(session.Success, session.Error);

        var taskWriteOnly = new ActorContext(
            ActorType.ApiToken, null, false, Guid.NewGuid(), null,
            projectId, null, ApiTokenCapability.TaskWrite);
        var result = await new LaunchSessionHandler(factory)
            .HandleAsync(new LaunchSessionCommand(
                new LaunchSessionRequest(session.Value!.Id), taskWriteOnly),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        using (TenantScope.EnterUnscoped())
        {
            var persisted = await _db.PipelineSessions.AsNoTracking()
                .SingleAsync(row => row.Id == session.Value.Id, TestContext.Current.CancellationToken);
            Assert.Equal(SessionStatus.Ready, persisted.Status);
        }
    }

    [Fact]
    public async Task RetryPipelineSession_RejectsRolePinnedHarnessToken()
    {
        var (projectId, task) = await SeedTaskAsync("Retry authorization");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));
        var spawner = new FakeSpawner();
        var planner = await new StartPipelineHandler(factory, spawner)
            .HandleAsync(new StartPipelineCommand(
                new StartPipelineRequest(task.Id), PipelineTestActor.AdminFor(projectId)),
                TestContext.Current.CancellationToken);
        Assert.True(planner.Success, planner.Error);

        using (TenantScope.EnterUnscoped())
        {
            var previous = await _db.PipelineSessions.SingleAsync(
                row => row.Id == planner.Value!.Id, TestContext.Current.CancellationToken);
            previous.Fail("planner crashed");
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var pinnedPlannerToken = new ActorContext(
            ActorType.ApiToken,
            UserId: null,
            IsAdmin: false,
            TokenId: Guid.NewGuid(),
            DeviceId: null,
            ProjectId: projectId,
            OrgId: null,
            Capabilities: ApiTokenCapability.SessionDrive,
            PipelineRole: PipelineRole.Planner,
            PipelineTaskId: task.Id,
            PipelineSessionId: planner.Value!.Id);
        var result = await new RetryPipelineSessionHandler(factory, spawner)
            .HandleAsync(new RetryPipelineSessionCommand(
                new RetryPipelineSessionRequest(planner.Value.Id), pinnedPlannerToken),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(1, await _db.PipelineSessions.CountAsync(
                row => row.TaskId == task.Id, TestContext.Current.CancellationToken));
        }
    }

}
