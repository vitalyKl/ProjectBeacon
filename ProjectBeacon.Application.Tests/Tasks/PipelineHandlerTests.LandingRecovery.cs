namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Infrastructure.Data;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

public sealed partial class PipelineHandlerTests
{
    [Fact]
    public async Task LandingCommandDispatch_UsesCompareAndSwapLeaseAndFindsExistingCommand()
    {
        var done = await SeedDoneSubtaskAsync("Recover landing command dispatch");
        var taskId = done.Task.Id;
        var deviceId = Guid.NewGuid();
        var requestedBy = Guid.NewGuid();
        var reviewRunId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            done.ProjectId,
            taskId,
            reviewRunId,
            requestedBy,
            deviceId,
            "main",
            new string('a', 40),
            new string('b', 40),
            new string('c', 64),
            "Verify the candidate before landing.");

        using (TenantScope.EnterUnscoped())
        {
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));
        await using var db = factory.CreateDbContext();
        var persisted = await db.LandingAttempts.SingleAsync(
            item => item.Id == attempt.Id, TestContext.Current.CancellationToken);

        Assert.False(LandingCommandDispatch.HasFreshLease(persisted, "Preview"));
        Assert.True(await LandingCommandDispatch.TryClaimAsync(
            db, persisted, "Preview", TestContext.Current.CancellationToken));
        // The same stale entity snapshot cannot claim the dispatch a second time.
        Assert.False(await LandingCommandDispatch.TryClaimAsync(
            db, persisted, "Preview", TestContext.Current.CancellationToken));

        await db.Entry(persisted).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(LandingCommandDispatch.HasFreshLease(persisted, "Preview"));
        Assert.Null(LandingCommandDispatch.PublicFailureCode(persisted));

        var command = Domain.Entities.Devices.WorkstationCommand.Create(
            deviceId,
            WorkstationCommandKind.GitLand,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                landingAttemptId = attempt.Id,
                action = "Preview"
            }),
            done.ProjectId,
            requestedBy);
        db.WorkstationCommands.Add(command);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var active = await LandingCommandDispatch.FindActiveCommandAsync(
            db, done.ProjectId, attempt.Id, "Preview", TestContext.Current.CancellationToken);
        Assert.NotNull(active);
        Assert.Equal(command.Id, active.Id);

        await LandingCommandDispatch.ClearLeaseAsync(
            db, persisted, "Preview", TestContext.Current.CancellationToken);
        await db.Entry(persisted).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Null(persisted.FailureCode);
        Assert.False(LandingCommandDispatch.HasFreshLease(persisted, "Preview"));
    }

    [Fact]
    public async Task LandingCommandDispatch_RecoversAnExpiredLeaseWithoutDuplicatingAnActiveCommand()
    {
        var done = await SeedDoneSubtaskAsync("Recover expired landing lease");
        var attempt = LandingAttempt.Start(
            done.ProjectId,
            done.Task.Id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "main",
            new string('a', 40),
            new string('b', 40),
            new string('c', 64),
            "Verify the candidate before landing.");
        attempt.MarkDispatchPending("Preview");

        using (TenantScope.EnterUnscoped())
        {
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));
        await using var db = factory.CreateDbContext();
        var persisted = await db.LandingAttempts.SingleAsync(
            item => item.Id == attempt.Id, TestContext.Current.CancellationToken);
        Assert.True(LandingCommandDispatch.HasFreshLease(persisted, "Preview"));
        Assert.False(await LandingCommandDispatch.TryClaimAsync(
            db, persisted, "Preview", TestContext.Current.CancellationToken));

        await db.LandingAttempts
            .Where(item => item.Id == attempt.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(
                item => item.UpdatedAt, DateTime.UtcNow.AddMinutes(-3)),
                TestContext.Current.CancellationToken);
        await db.Entry(persisted).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.False(LandingCommandDispatch.HasFreshLease(persisted, "Preview"));
        Assert.True(await LandingCommandDispatch.TryClaimAsync(
            db, persisted, "Preview", TestContext.Current.CancellationToken));
        await db.Entry(persisted).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(LandingCommandDispatch.HasFreshLease(persisted, "Preview"));
    }

    [Fact]
    public async Task StartReview_AfterLatestLandingConflict_RequiresFreshReviewWithoutDiscardingCompletedSubtasks()
    {
        var done = await SeedDoneSubtaskAsync("Re-review conflicted landing");
        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));
        var spawner = new FakeSpawner();

        var firstReview = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(firstReview.Success, firstReview.Error);

        var firstApproval = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(
                done.Task.Id, ReviewVerdictKind.Approve, "Original approval")),
                TestContext.Current.CancellationToken);
        Assert.True(firstApproval.Success, firstApproval.Error);
        Assert.Equal(TaskPipelineStage.Approved, firstApproval.Value!.Stage);

        Guid reviewRunId;
        Guid deviceId;
        using (TenantScope.EnterUnscoped())
        {
            reviewRunId = await _db.ReviewRuns.IgnoreQueryFilters()
                .Where(run => run.ProjectId == done.ProjectId && run.TaskId == done.Task.Id)
                .Select(run => run.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
            deviceId = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(runtime => runtime.ProjectId == done.ProjectId)
                .Select(runtime => runtime.DeviceId)
                .SingleAsync(TestContext.Current.CancellationToken);

            var failedAttempt = LandingAttempt.Start(
                done.ProjectId, done.Task.Id, reviewRunId, Guid.NewGuid(), deviceId,
                "main", new string('a', 40), new string('b', 40), new string('c', 64),
                "An earlier preview failed and must not reopen review.");
            failedAttempt.RecordPreview(
                "main", new string('d', 40), null, null, null, null);
            Assert.Equal(LandingAttemptStatus.Failed, failedAttempt.Status);
            _db.LandingAttempts.Add(failedAttempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var blocked = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)),
                TestContext.Current.CancellationToken);
        Assert.False(blocked.Success);
        Assert.Contains("latest landing attempt is conflicted", blocked.Error, StringComparison.OrdinalIgnoreCase);

        Guid conflictAttemptId;
        using (TenantScope.EnterUnscoped())
        {
            var conflictedAttempt = LandingAttempt.Start(
                done.ProjectId, done.Task.Id, reviewRunId, Guid.NewGuid(), deviceId,
                "main", new string('e', 40), new string('f', 40), new string('1', 64),
                "The task branch must be integrated and reviewed again.");
            conflictedAttempt.RecordPreview(
                "main", new string('2', 40), null, null, null,
                System.Text.Json.JsonSerializer.Serialize(new[] { "src/conflict.cs" }));
            Assert.Equal(LandingAttemptStatus.Conflicted, conflictedAttempt.Status);
            conflictAttemptId = conflictedAttempt.Id;
            _db.LandingAttempts.Add(conflictedAttempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var reopenedReview = await new StartReviewHandler(factory, spawner)
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(done.Task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(reopenedReview.Success, reopenedReview.Error);
        Assert.NotEqual(firstReview.Value!.Id, reopenedReview.Value!.Id);

        var pipeline = await new GetPipelineHandler(factory)
            .HandleAsync(new GetPipelineCommand(new GetPipelineRequest(done.Task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(pipeline.Success, pipeline.Error);
        Assert.Equal(TaskPipelineStage.Reviewing, pipeline.Value!.Stage);
        Assert.Equal(SubtaskStatus.Done, pipeline.Value.Subtasks.Single().Status);
        Assert.Equal(2, spawner.Calls.Count(call => call.Role == PipelineRole.Review));

        var secondApproval = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(
                done.Task.Id, ReviewVerdictKind.Approve, "Approval after conflict resolution")),
                TestContext.Current.CancellationToken);
        Assert.True(secondApproval.Success, secondApproval.Error);
        Assert.Equal(TaskPipelineStage.Approved, secondApproval.Value!.Stage);

        using (TenantScope.EnterUnscoped())
        {
            var reviewRuns = await _db.ReviewRuns.IgnoreQueryFilters()
                .Where(run => run.ProjectId == done.ProjectId && run.TaskId == done.Task.Id)
                .OrderByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.Id)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, reviewRuns.Count);
            Assert.Equal(reopenedReview.Value.Id, reviewRuns[0].TargetRunId);

            var persistedConflict = await _db.LandingAttempts.IgnoreQueryFilters()
                .SingleAsync(attempt => attempt.Id == conflictAttemptId, TestContext.Current.CancellationToken);
            Assert.Equal(LandingAttemptStatus.Conflicted, persistedConflict.Status);
        }
    }
}
