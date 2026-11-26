namespace ProjectBeacon.Application.Tests;

using Application.Common;
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
        Guid deviceId;
        Guid requestedBy;
        using (TenantScope.EnterUnscoped())
        {
            deviceId = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(runtime => runtime.ProjectId == done.ProjectId)
                .Select(runtime => runtime.DeviceId)
                .FirstAsync(TestContext.Current.CancellationToken);
            requestedBy = await _db.DaemonDevices.IgnoreQueryFilters()
                .Where(device => device.Id == deviceId)
                .Select(device => device.UserId)
                .FirstAsync(TestContext.Current.CancellationToken);
        }
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
        persisted.SetEscalationCode("ManualExecutionRequired");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(LandingCommandDispatch.HasFreshLease(persisted, "Preview"));
        Assert.True(await LandingCommandDispatch.TryClaimAsync(
            db, persisted, "Preview", TestContext.Current.CancellationToken));
        // The same stale entity snapshot cannot claim the dispatch a second time.
        Assert.False(await LandingCommandDispatch.TryClaimAsync(
            db, persisted, "Preview", TestContext.Current.CancellationToken));

        await db.Entry(persisted).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(LandingCommandDispatch.HasFreshLease(persisted, "Preview"));
        Assert.Null(persisted.EscalationCode);
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
    public async Task ExecuteLanding_DoesNotRedispatchAnAlreadyPendingGitHubMerge()
    {
        var done = await SeedDoneSubtaskAsync("Do not redispatch a GitHub queued merge");
        Guid deviceId;
        Guid requestedBy;
        using (TenantScope.EnterUnscoped())
        {
            deviceId = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(runtime => runtime.ProjectId == done.ProjectId)
                .Select(runtime => runtime.DeviceId)
                .FirstAsync(TestContext.Current.CancellationToken);
            requestedBy = await _db.DaemonDevices.IgnoreQueryFilters()
                .Where(device => device.Id == deviceId)
                .Select(device => device.UserId)
                .FirstAsync(TestContext.Current.CancellationToken);
        }

        var attempt = LandingAttempt.Start(
            done.ProjectId, done.Task.Id, Guid.NewGuid(), requestedBy, deviceId,
            "main", new string('a', 40), new string('b', 40), new string('c', 64),
            "GitHub has already accepted the pull request into its merge queue.");
        attempt.RecordPreview("main", new string('d', 40), new string('e', 40), new string('f', 64), 0, null);
        attempt.MarkExecutePending();
        var mergeRequestId = Guid.NewGuid().ToString("D");
        attempt.MarkGitHubMergeQueued(mergeRequestId, "pending");
        using (TenantScope.EnterUnscoped())
        {
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));
        var actor = HumanActor(requestedBy);
        var result = await new ExecuteLandingHandler(factory, new Application.Devices.EnqueueCommandHandler(factory))
            .HandleAsync(new ExecuteLandingCommand(
                new ExecuteLandingRequest(done.Task.Id, attempt.Id, Automatic: false), actor),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(LandingAttemptStatus.ExecutePending, result.Value!.Status);
        Assert.Equal("GitHubMergePending:" + mergeRequestId, result.Value.FailureCode);
        using (TenantScope.EnterUnscoped())
        {
            var persisted = await _db.LandingAttempts.AsNoTracking()
                .SingleAsync(item => item.Id == attempt.Id, TestContext.Current.CancellationToken);
            Assert.Equal("GitHubMergePending:" + mergeRequestId, persisted.FailureCode);
            Assert.Empty(await _db.WorkstationCommands.AsNoTracking()
                .Where(command => command.Kind == WorkstationCommandKind.GitLand
                    && command.PayloadJson.Contains(attempt.Id.ToString("D")))
                .ToListAsync(TestContext.Current.CancellationToken));
        }
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
        attempt.SetGitPolicySnapshot(1, """{"autonomyLevel":0,"dirtyCheckoutBehavior":"Escalate","protectedPaths":[],"maxChangedFiles":200,"maxChangedLines":20000,"autoRevertMode":"Off"}""");
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
    public async Task LandingCommandDispatch_AllowsExplicitRetryOfRevertFailedAttempt()
    {
        var done = await SeedDoneSubtaskAsync("Retry failed landing revert");
        Guid deviceId;
        Guid requestedBy;
        using (TenantScope.EnterUnscoped())
        {
            deviceId = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(runtime => runtime.ProjectId == done.ProjectId)
                .Select(runtime => runtime.DeviceId)
                .FirstAsync(TestContext.Current.CancellationToken);
            requestedBy = await _db.DaemonDevices.IgnoreQueryFilters()
                .Where(device => device.Id == deviceId)
                .Select(device => device.UserId)
                .FirstAsync(TestContext.Current.CancellationToken);
        }

        var attempt = LandingAttempt.Start(
            done.ProjectId, done.Task.Id, Guid.NewGuid(), requestedBy, deviceId,
            "main", new string('a', 40), new string('b', 40), new string('c', 64),
            "Retry a revert after confirming the target ref.");
        attempt.RecordPreview("main", new string('d', 40), new string('e', 40), new string('f', 64), 0, null);
        attempt.MarkExecutePending();
        attempt.RecordLanded(new string('1', 40), new string('e', 40), 0, new string('2', 64));
        attempt.MarkRevertPending();
        attempt.Fail("TargetAdvancedBeyondLanding");

        using (TenantScope.EnterUnscoped())
        {
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var factory = HandlerSqlite.Factory(_connection, Scope(done.ProjectId));
        await using var db = factory.CreateDbContext();
        var persisted = await db.LandingAttempts.SingleAsync(
            item => item.Id == attempt.Id, TestContext.Current.CancellationToken);

        Assert.True(await LandingCommandDispatch.TryClaimAsync(
            db, persisted, "Revert", TestContext.Current.CancellationToken));
        await db.Entry(persisted).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(LandingAttemptStatus.RevertPending, persisted.Status);
        Assert.True(LandingCommandDispatch.HasFreshLease(persisted, "Revert"));
        Assert.False(await LandingCommandDispatch.TryClaimAsync(
            db, persisted, "Revert", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestLanding_RejectsReviewProofProducedOnAnotherWorkstation()
    {
        var (projectId, task) = await SeedTaskAsync("Reject landing proof from another workstation");
        var factory = HandlerSqlite.Factory(_connection, Scope(projectId));

        // Attach both stations before the pipeline creates a persistent task workspace.
        // The latest eligible station is selected and pinned by StartPipeline; the first
        // station remains attached but must not be accepted as the review proof producer.
        Guid otherDeviceId;
        Guid otherOwnerId;
        using (TenantScope.EnterUnscoped())
        {
            var firstRuntime = await _db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(runtime => runtime.ProjectId == projectId)
                .SingleAsync(TestContext.Current.CancellationToken);
            otherDeviceId = firstRuntime.DeviceId;
            otherOwnerId = await _db.DaemonDevices.IgnoreQueryFilters()
                .Where(device => device.Id == otherDeviceId)
                .Select(device => device.UserId)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        var (pinnedUser, pinnedDevice) = await SeedOnlineRuntimeAsync(projectId);
        var start = await new StartPipelineHandler(factory, new FakeSpawner())
            .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(start.Success, start.Error);

        var subtask = await new CreateSubtaskHandler(factory)
            .HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(task.Id, "Complete work for landing proof.")),
                TestContext.Current.CancellationToken);
        Assert.True(subtask.Success, subtask.Error);

        var reported = await new ReportSubtaskResultHandler(factory)
            .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(
                task.Id, subtask.Value!.Id, "diff://landing-proof-test", "Implementation completed.")),
                TestContext.Current.CancellationToken);
        Assert.True(reported.Success, reported.Error);

        var review = await new StartReviewHandler(factory, new FakeSpawner())
            .HandleAsync(new StartReviewCommand(new StartReviewRequest(task.Id)),
                TestContext.Current.CancellationToken);
        Assert.True(review.Success, review.Error);

        var verdict = await new RecordReviewVerdictHandler(factory)
            .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(
                task.Id, ReviewVerdictKind.Approve, "Approve the task branch.")),
                TestContext.Current.CancellationToken);
        Assert.True(verdict.Success, verdict.Error);
        Assert.Equal(TaskPipelineStage.Approved, verdict.Value!.Stage);

        await using (var projectDb = factory.CreateDbContext())
        {
            var updatedProject = await projectDb.Projects.IgnoreQueryFilters()
                .Where(project => project.Id == projectId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    project => project.ReviewCheckCommand, "dotnet test"),
                    TestContext.Current.CancellationToken);
            Assert.Equal(1, updatedProject);

            var configuredCommand = await projectDb.Projects.IgnoreQueryFilters()
                .Where(project => project.Id == projectId)
                .Select(project => project.ReviewCheckCommand)
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("dotnet test", configuredCommand);
        }
        var commandHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("dotnet test"))).ToLowerInvariant();

        using (TenantScope.EnterUnscoped())
        {
            var reviewRun = await _db.ReviewRuns.IgnoreQueryFilters()
                .SingleAsync(run => run.TaskId == task.Id, TestContext.Current.CancellationToken);
            reviewRun.SetCheckCommandHash(commandHash);
            reviewRun.CompleteCheck(
                "All tests passed.",
                "review-artifact",
                new string('a', 40),
                new string('b', 40),
                otherDeviceId,
                "main");
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var persistedTask = await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.Equal(pinnedDevice.Id, persistedTask.PipelineDeviceId);
            Assert.NotEqual(pinnedDevice.Id, otherDeviceId);
            Assert.NotEqual(otherOwnerId, pinnedUser.Id);
        }

        var landing = await new RequestLandingHandler(
                factory, new Application.Devices.EnqueueCommandHandler(factory))
            .HandleAsync(new RequestLandingCommand(
                new RequestLandingRequest(task.Id, "The proof device must match the task workspace."),
                HumanActor(otherOwnerId)),
                TestContext.Current.CancellationToken);

        Assert.False(landing.Success);
        Assert.Equal(ErrorKind.Conflict, landing.Kind);
        Assert.Contains("other than the one pinned", landing.Error, StringComparison.OrdinalIgnoreCase);

        using (TenantScope.EnterUnscoped())
        {
            Assert.Empty(await _db.LandingAttempts.IgnoreQueryFilters().AsNoTracking()
                .Where(attempt => attempt.ProjectId == projectId && attempt.TaskId == task.Id)
                .ToListAsync(TestContext.Current.CancellationToken));
            var persistedTask = await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(item => item.Id == task.Id, TestContext.Current.CancellationToken);
            Assert.Equal(pinnedDevice.Id, persistedTask.PipelineDeviceId);
        }
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
