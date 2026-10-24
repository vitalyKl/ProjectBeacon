namespace ProjectBeacon.API.Tests;

using System.Text.Json.Nodes;
using Application.Authorization;
using Application.Chat;
using Application.Common;
using Application.Devices;
using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Chat;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
/// Genuine PostgreSQL concurrency tests: two truly parallel handler calls race the same
/// guarded transition. Exactly one may win, and the loser's side effects must not persist.
/// </summary>
[Collection("postgres-serial")]
public sealed class ConcurrencyPostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public ConcurrencyPostgresTests(PostgresFixture postgres) => _postgres = postgres;

    private sealed class CountingSpawner : ISessionSpawner
    {
        private int _calls;

        public int Calls
        {
            get { lock (this) return _calls; }
        }

        public Task<SpawnedSession> SpawnAsync(IBeaconDb db, Guid projectId, PipelineRole role, Guid taskId, CancellationToken ct)
        {
            lock (this) _calls++;
            return Task.FromResult(new SpawnedSession(null, null, null));
        }
    }

    [Fact]
    public async Task ConcurrentStartPipeline_OnlyOneStarts()
    {
        _postgres.RequirePostgres();
        var (_, taskId) = await SeedTaskAsync("start race");

        var spawner = new CountingSpawner();
        var factory = _postgres.CreateFactory();
        async Task<Result<PipelineSessionDto>> Run()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new StartPipelineHandler(factory, spawner)
                    .HandleAsync(new StartPipelineCommand(new StartPipelineRequest(taskId)));
            }
        }

        var results = await Task.WhenAll(Run(), Run());

        Assert.Equal(1, results.Count(r => r.Success));
        var loser = results.Single(r => !r.Success);
        // Pre-commit read loses at the claim; a post-commit read loses at the domain check.
        Assert.Contains(loser.Error,
            new[] { "Pipeline state changed while starting; retry.", "Pipeline is already started (stage: Planning)" });
        Assert.Equal(1, spawner.Calls);

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(1, await db.PipelineSessions.CountAsync(s => s.TaskId == taskId, TestContext.Current.CancellationToken));
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskPipelineStage.Planning, row.PipelineStage);
            Assert.Equal(TaskItemStatus.InProgress, row.Status);
        }
    }

    [Fact]
    public async Task ConcurrentTaskStatusChange_NoStaleOverwrite()
    {
        _postgres.RequirePostgres();
        var (projectId, taskId) = await SeedTaskAsync("status race");

        var factory = _postgres.CreateFactory();
        async Task<Result<TaskItemDto>> Run()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new ChangeTaskStatusHandler(factory)
                    .HandleAsync(new ChangeTaskStatusCommand(new ChangeTaskStatusRequest(taskId, TaskItemStatus.InProgress, projectId)));
            }
        }

        var results = await Task.WhenAll(Run(), Run());
        Assert.Contains(results, r => r.Success);

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.InProgress, row.Status);
            Assert.Null(row.CompletedAt);
        }
    }

    [Fact]
    public async Task ConcurrentReviewVerdict_OnlyOneRecords()
    {
        _postgres.RequirePostgres();
        Guid taskId, projectId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("Verdict Race Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("Verdict Race Project", null, org.Id);
            seed.Projects.Add(project);
            var task = TaskItem.Create("verdict race", project.Id);
            task.StartPipeline();
            task.EnterExecuting();
            task.EnterReview();
            seed.Tasks.Add(task);
            seed.PipelineSessions.Add(PipelineSession.Create(task.Id, project.Id, PipelineRole.Review, "review prompt"));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            projectId = project.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result<PipelineStateDto>> Run()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new RecordReviewVerdictHandler(factory)
                    .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(taskId, ReviewVerdictKind.Approve, "looks good")));
            }
        }

        var results = await Task.WhenAll(Run(), Run());

        Assert.Equal(1, results.Count(r => r.Success));
        var loser = results.Single(r => !r.Success);
        Assert.Contains(loser.Error,
            new[] { "Review state changed while recording verdict; retry.", "Cannot record verdict in stage Approved" });

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(1, await db.ReviewVerdicts.CountAsync(v => v.TaskId == taskId, TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.ReviewRuns.CountAsync(r => r.TaskId == taskId, TestContext.Current.CancellationToken));
            var session = await db.PipelineSessions.FirstAsync(s => s.TaskId == taskId && s.Role == PipelineRole.Review, TestContext.Current.CancellationToken);
            Assert.Equal(SessionStatus.Closed, session.Status);
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskPipelineStage.Approved, row.PipelineStage);
        }
    }

    [Fact]
    public async Task ConcurrentSubtaskReport_OnlyOneReports()
    {
        _postgres.RequirePostgres();
        Guid taskId, subtaskId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("Subtask Race Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("Subtask Race Project", null, org.Id);
            seed.Projects.Add(project);
            var task = TaskItem.Create("subtask race", project.Id);
            task.StartPipeline();
            task.EnterExecuting();
            seed.Tasks.Add(task);
            var sub = Subtask.Create("do the work", task.Id, project.Id);
            sub.Start();
            seed.Subtasks.Add(sub);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            subtaskId = sub.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result<PipelineStateDto>> Run(string summary)
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new ReportSubtaskResultHandler(factory)
                    .HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(taskId, subtaskId, "diff-ref", summary)));
            }
        }

        var results = await Task.WhenAll(Run("summary-a"), Run("summary-b"));

        Assert.Equal(1, results.Count(r => r.Success));
        var (winner, loser) = results[0].Success ? (results[0], results[1]) : (results[1], results[0]);
        Assert.Contains(loser.Error,
            new[] { "Subtask state changed while reporting; retry.", "Subtask is already finished." });

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.Subtasks.FirstAsync(s => s.Id == subtaskId, TestContext.Current.CancellationToken);
            Assert.Equal(SubtaskStatus.Done, row.Status);
            Assert.Equal(winner.Value!.Subtasks.Single(s => s.Id == subtaskId).Summary, row.Summary);
        }
    }

    [Fact]
    public async Task ConcurrentCommandCompletion_OnlyOneCompletes()
    {
        _postgres.RequirePostgres();
        Guid deviceId, commandId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("completion-user", "completion-user@beacon.local", "hash");
            seed.Users.Add(user);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var device = DaemonDevice.Create("completion device", user.Id, "fp-" + Guid.NewGuid().ToString("N"), "hash-" + Guid.NewGuid().ToString("N"), "bcd_completion");
            seed.DaemonDevices.Add(device);
            var command = WorkstationCommand.Create(device.Id, WorkstationCommandKind.ListDir, "{}");
            seed.WorkstationCommands.Add(command);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            deviceId = device.Id;
            commandId = command.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result<WorkstationCommandDto>> Run(string resultJson)
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new CompleteCommandHandler(factory)
                    .HandleAsync(new CompleteCommandCommand(new CompleteCommandRequest(commandId, deviceId, true, resultJson, null)));
            }
        }

        var results = await Task.WhenAll(Run("{\"ok\":\"a\"}"), Run("{\"ok\":\"b\"}"));

        Assert.Equal(1, results.Count(r => r.Success));
        var (winner, loser) = results[0].Success ? (results[0], results[1]) : (results[1], results[0]);
        Assert.Contains(loser.Error,
            new[] { "Command state changed while completing; retry.", "Command already finished." });

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.WorkstationCommands.FirstAsync(c => c.Id == commandId, TestContext.Current.CancellationToken);
            Assert.Equal(WorkstationCommandStatus.Succeeded, row.Status);
            Assert.Equal(winner.Value!.ResultJson, row.ResultJson);
        }
    }

    [Fact]
    public async Task ConcurrentReviewCheck_OnlyOneEnqueues()
    {
        _postgres.RequirePostgres();
        Guid userId, deviceId, taskId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("check-user", "check-user@beacon.local", "hash");
            seed.Users.Add(user);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var org = Org.Create("Check Race Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("Check Race Project", null, org.Id);
            seed.Projects.Add(project);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            var device = DaemonDevice.Create("check device", user.Id, "fp-" + Guid.NewGuid().ToString("N"), "hash-" + Guid.NewGuid().ToString("N"), "bcd_check");
            device.Heartbeat();
            seed.DaemonDevices.Add(device);
            seed.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, device.Id, @"A:\work\check"));
            var task = TaskItem.Create("check race", project.Id);
            seed.Tasks.Add(task);
            seed.ReviewRuns.Add(ReviewRun.Start(project.Id, task.Id, ReviewerType.Agent, Guid.NewGuid()));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
            deviceId = device.Id;
            taskId = task.Id;
        }

        var factory = _postgres.CreateFactory();
        var enqueue = new EnqueueCommandHandler(factory);
        var human = new ActorContext(ActorType.Human, userId, false, null, null, null, null, ApiTokenCapability.None);
        async Task<Result<PipelineStateDto>> Run()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new RecordReviewCheckHandler(factory, enqueue)
                    .HandleAsync(new RecordReviewCheckCommand(
                        new RecordReviewCheckRequest(taskId, deviceId, userId, "dotnet test"), human));
            }
        }

        var results = await Task.WhenAll(Run(), Run());

        Assert.Equal(1, results.Count(r => r.Success));
        // The claim is on ReviewerActorId, so even a post-commit read loses at the claim.
        Assert.Equal("A review check is already in flight for this run.", results.Single(r => !r.Success).Error);

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            Assert.Equal(1, await db.WorkstationCommands
                .CountAsync(c => c.DeviceId == deviceId && c.Kind == WorkstationCommandKind.RunReviewCheck, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ConcurrentSaveWorkstation_SameBase_OnlyOneLands_OthersConflict()
    {
        _postgres.RequirePostgres();
        Guid userId, deviceId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("save-user", "save-user@beacon.local", "hash");
            seed.Users.Add(user);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var device = DaemonDevice.Create("save device", user.Id, "fp-" + Guid.NewGuid().ToString("N"), "hash-" + Guid.NewGuid().ToString("N"), "bcd_save");
            device.Heartbeat();
            seed.DaemonDevices.Add(device);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
            deviceId = device.Id;
        }

        var factory = _postgres.CreateFactory();
        // All three saves claim the same base revision: exactly one may win the guarded
        // update; the others must conflict and leave no side effects behind.
        string[] payloads = ["""{"n":"a","revision":0}""", """{"n":"b","revision":0}""", """{"n":"c","revision":0}"""];
        async Task<Result<WorkstationCommandDto>> Run(string payload)
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new EnqueueCommandHandler(factory)
                    .HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(deviceId, userId, WorkstationCommandKind.SaveWorkstation, payload, null)));
            }
        }

        var results = await Task.WhenAll(Run(payloads[0]), Run(payloads[1]), Run(payloads[2]));
        Assert.Single(results, r => r.Success);
        Assert.Equal(2, results.Count(r => r.Kind == ErrorKind.Conflict));
        var winner = results.Single(r => r.Success);
        var winningPayload = JsonNode.Parse(winner.Value!.PayloadJson!)!.AsObject();
        Assert.Equal(1L, winningPayload["revision"]!.GetValue<long>());

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.DaemonDevices.FirstAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(1, row.DesiredRevision);

            // Only the winner enqueued a command, and the stored JSON pairs with its payload.
            Assert.Equal(1, await db.WorkstationCommands.CountAsync(c => c.DeviceId == deviceId && c.Kind == WorkstationCommandKind.SaveWorkstation, TestContext.Current.CancellationToken));
            winningPayload.Remove("revision");
            Assert.True(JsonNode.DeepEquals(winningPayload, JsonNode.Parse(row.DesiredWorkstationJson)));
        }
    }

    [Fact]
    public async Task SaveWorkstation_InsertFailure_RollsBackJsonAndRevision_AllowsRetry()
    {
        _postgres.RequirePostgres();
        Guid userId, deviceId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("inject-user", "inject-user@beacon.local", "hash");
            seed.Users.Add(user);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var device = DaemonDevice.Create("inject device", user.Id, "fp-" + Guid.NewGuid().ToString("N"), "hash-" + Guid.NewGuid().ToString("N"), "bcd_inject");
            device.Heartbeat();
            seed.DaemonDevices.Add(device);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
            deviceId = device.Id;

            // Fault injection at the database level: refuse the command insert for marked
            // payloads. Created as superuser on the disposable fixture database only.
            await seed.Database.ExecuteSqlRawAsync(@"
                CREATE FUNCTION beacon_inject_fail() RETURNS trigger AS $$
                BEGIN
                    IF NEW.""PayloadJson"" LIKE '%injectFail%' THEN
                        RAISE EXCEPTION 'injected insert failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER beacon_inject_fail
                BEFORE INSERT ON ""WorkstationCommands""
                FOR EACH ROW EXECUTE FUNCTION beacon_inject_fail();", TestContext.Current.CancellationToken);
        }
        try
        {
            var factory = _postgres.CreateFactory();
            using (TenantScope.EnterUnscoped())
            {
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    new EnqueueCommandHandler(factory).HandleAsync(
                        new EnqueueCommandCommand(new EnqueueCommandRequest(
                            deviceId, userId, WorkstationCommandKind.SaveWorkstation,
                            """{"n":"a","revision":0,"injectFail":true}""", null)), TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            await using var cleanup = _postgres.CreateAdminContext();
            await cleanup.Database.ExecuteSqlRawAsync(@"
                DROP TRIGGER IF EXISTS beacon_inject_fail ON ""WorkstationCommands"";
                DROP FUNCTION IF EXISTS beacon_inject_fail();", TestContext.Current.CancellationToken);
        }

        // The guarded UPDATE and the INSERT roll back together: JSON, revision, and the
        // queue are exactly as before the failed save.
        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.DaemonDevices.FirstAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(0, row.DesiredRevision);
            Assert.Equal("{}", row.DesiredWorkstationJson);
            Assert.Equal(0, await db.WorkstationCommands.CountAsync(c => c.DeviceId == deviceId, TestContext.Current.CancellationToken));
        }

        // The old base revision is still valid: the retry without the fault lands revision 1.
        using (TenantScope.EnterUnscoped())
        {
            var retry = await new EnqueueCommandHandler(_postgres.CreateFactory()).HandleAsync(
                new EnqueueCommandCommand(new EnqueueCommandRequest(
                    deviceId, userId, WorkstationCommandKind.SaveWorkstation, """{"n":"a","revision":0}""", null)), TestContext.Current.CancellationToken);
            Assert.True(retry.Success, retry.Error);
            var row = await db.DaemonDevices.AsNoTracking().FirstAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(1, row.DesiredRevision);
            Assert.Equal("""{"n":"a"}""", row.DesiredWorkstationJson);
            Assert.Equal(1, await db.WorkstationCommands.CountAsync(c => c.DeviceId == deviceId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ConcurrentBumpUser_AllBumpsLand()
    {
        _postgres.RequirePostgres();
        Guid userId, deviceId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var user = User.Create("bump-user", "bump-user@beacon.local", "hash");
            seed.Users.Add(user);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var device = DaemonDevice.Create("bump device", user.Id, "fp-" + Guid.NewGuid().ToString("N"), "hash-" + Guid.NewGuid().ToString("N"), "bcd_bump");
            seed.DaemonDevices.Add(device);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
            deviceId = device.Id;
        }

        async Task Bump()
        {
            using (TenantScope.EnterUnscoped())
            {
                await using var db = _postgres.CreateAdminContext();
                await DesiredState.BumpUserAsync(db, userId, default);
            }
        }

        await Task.WhenAll(Bump(), Bump(), Bump());

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.DaemonDevices.FirstAsync(d => d.Id == deviceId, TestContext.Current.CancellationToken);
            Assert.Equal(3, row.DesiredRevision);
        }
    }

    [Fact]
    public async Task ConcurrentStartActorSession_OnlyOneSpawn_OneActorSession()
    {
        _postgres.RequirePostgres();
        Guid taskId, subtaskId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("Actor Race Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("Actor Race Project", null, org.Id);
            seed.Projects.Add(project);
            var task = TaskItem.Create("actor race", project.Id);
            task.StartPipeline();
            task.EnterExecuting();
            seed.Tasks.Add(task);
            var sub = Subtask.Create("do the work", task.Id, project.Id);
            seed.Subtasks.Add(sub);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            subtaskId = sub.Id;
        }

        var spawner = new CountingSpawner();
        var factory = _postgres.CreateFactory();
        async Task<Result<PipelineSessionDto>> Run()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new StartActorSessionHandler(factory, spawner)
                    .HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(taskId, subtaskId)));
            }
        }

        var results = await Task.WhenAll(Run(), Run());

        Assert.Contains(results, r => r.Success);
        // Both may succeed only when the second start's pre-read saw the claim and the
        // open session; it must then return the winner's session, never spawn again.
        var okIds = results.Where(r => r.Success).Select(r => r.Value!.Id).Distinct().ToList();
        Assert.Single(okIds);
        Assert.Equal(1, spawner.Calls);

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var actorSessions = await db.PipelineSessions
                .Where(s => s.TaskId == taskId && s.Role == PipelineRole.Actor).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Single(actorSessions);
            Assert.Equal(okIds[0], actorSessions[0].Id);
            var row = await db.Subtasks.FirstAsync(s => s.Id == subtaskId, TestContext.Current.CancellationToken);
            Assert.Equal(SubtaskStatus.InProgress, row.Status);
        }
    }

    [Fact]
    public async Task ConcurrentChangeTaskStatus_ToDone_BothOk()
    {
        _postgres.RequirePostgres();
        Guid taskId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("Done Race Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("Done Race Project", null, org.Id);
            seed.Projects.Add(project);
            var task = TaskItem.Create("done race", project.Id);
            task.SetReviewNotes("reviewed");
            seed.Tasks.Add(task);
            var run = ReviewRun.Start(project.Id, task.Id, ReviewerType.Agent, Guid.NewGuid());
            run.Complete("checked", "check:exit0");
            seed.ReviewRuns.Add(run);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result<TaskItemDto>> Run()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new ChangeTaskStatusHandler(factory)
                    .HandleAsync(new ChangeTaskStatusCommand(new ChangeTaskStatusRequest(taskId, TaskItemStatus.Done, null)));
            }
        }

        var results = await Task.WhenAll(Run(), Run());

        // The loser's re-read sees the same target status and returns Ok idempotently.
        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.Done, row.Status);
        }
    }

    [Fact]
    public async Task ConcurrentChangeTaskStatus_MixedTargets_ClassifiedDeterministically()
    {
        _postgres.RequirePostgres();
        Guid taskId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("Mix Race Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("Mix Race Project", null, org.Id);
            seed.Projects.Add(project);
            var task = TaskItem.Create("mixed race", project.Id);
            task.SetReviewNotes("reviewed");
            task.TransitionTo(TaskItemStatus.InProgress);
            seed.Tasks.Add(task);
            var run = ReviewRun.Start(project.Id, task.Id, ReviewerType.Agent, Guid.NewGuid());
            run.Complete("checked", "check:exit0");
            seed.ReviewRuns.Add(run);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result<TaskItemDto>> Run(TaskItemStatus target)
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new ChangeTaskStatusHandler(factory)
                    .HandleAsync(new ChangeTaskStatusCommand(new ChangeTaskStatusRequest(taskId, target, null)));
            }
        }

        var results = await Task.WhenAll(Run(TaskItemStatus.Done), Run(TaskItemStatus.Todo));

        // Every outcome is a winner or a classified conflict — never a silent stale write.
        Assert.All(results, r =>
            Assert.True(r.Success || r.Kind == ErrorKind.Conflict, $"{r.Success} {r.Kind}: {r.Error}"));

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
            Assert.True(row.Status is TaskItemStatus.Todo or TaskItemStatus.Done);
            if (results.All(r => r.Success))
                Assert.Contains(row.Status.ToString(), results.Select(r => r.Value!.Status));
            else
            {
                var winner = results.Single(r => r.Success);
                Assert.Equal(winner.Value!.Status, row.Status.ToString());
            }
        }
    }

    [Fact]
    public async Task ConcurrentFinishWorkDone_DifferentReviewRuns_BothOk_SingleComment()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId, runA, runB;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("FW Done Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("FW Done Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-done-user", "fw-done-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            var task = TaskItem.Create("fw done race", project.Id);
            seed.Tasks.Add(task);
            var a = ReviewRun.Start(project.Id, task.Id, ReviewerType.Agent, Guid.NewGuid());
            a.Complete("checked", "check:exit0");
            var b = ReviewRun.Start(project.Id, task.Id, ReviewerType.Agent, Guid.NewGuid());
            b.Complete("checked", "check:exit0");
            seed.ReviewRuns.Add(a);
            seed.ReviewRuns.Add(b);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            userId = user.Id;
            runA = a.Id;
            runB = b.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result> Run(string output, Guid runId)
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new FinishWorkHandler(factory)
                    .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "done", output, userId.ToString(), ReviewRunId: runId)));
            }
        }

        var results = await Task.WhenAll(Run("notes-a", runA), Run("notes-b", runB));

        // The loser's re-read sees a Done task and returns Ok — the comment is winner-only.
        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.Done, row.Status);
            Assert.Contains(row.ReviewNotes, new[] { "notes-a", "notes-b" });
            Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ConcurrentFinishWorkFailed_DifferentOutputs_OneWinner_LoserConflicts_RetryLands()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("FW Fail Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("FW Fail Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-fail-user", "fw-fail-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("fw fail race", project.Id);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            userId = user.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result> Run(string output)
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new FinishWorkHandler(factory)
                    .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "failed", output, userId.ToString())));
            }
        }

        // Both reports read a Todo task, and a failed report keeps the task Todo, so the
        // status guard alone cannot pick a winner. The state revision does: exactly one
        // guarded update matches revision 0; the rival gets a conflict and no side effects.
        var results = await Task.WhenAll(Run("X"), Run("Y"));
        var winnerIndex = results[0].Success ? 0 : 1;
        Assert.Equal(1, results.Count(r => r.Success));
        var loser = results.Single(r => !r.Success);
        Assert.Equal(ErrorKind.Conflict, loser.Kind);

        var loserOutput = winnerIndex == 0 ? "Y" : "X";
        await using (var db = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                Assert.Equal(TaskItemStatus.Todo, row.Status);
                var parts = (row.Description ?? "").Split("\n\n");
                Assert.Equal(1, parts.Count(p => p == "Failed: " + (loserOutput == "Y" ? "X" : "Y")));
                Assert.Equal(0, parts.Count(p => p == "Failed: " + loserOutput));
                Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
            }
        }

        // The losing report retries with a fresh read: its note and comment land.
        Assert.True((await Run(loserOutput)).Success);

        // A sequential replay of the same report is suppressed: no duplicate note or comment.
        Assert.True((await Run(loserOutput)).Success);
        await using (var db = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                var after = (row.Description ?? "").Split("\n\n");
                Assert.Equal(1, after.Count(p => p == "Failed: X"));
                Assert.Equal(1, after.Count(p => p == "Failed: Y"));
                Assert.Equal(2, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
            }
        }
    }

    [Fact]
    public async Task ConcurrentFinishWorkDoneVsFailed_FromTodo_ExactlyOneWinner_RetryLands()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId, runId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("FW Mix Todo Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("FW Mix Todo Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-mix-todo-user", "fw-mix-todo-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("fw mix todo race", project.Id);
            seed.Tasks.Add(task);
            var run = ReviewRun.Start(project.Id, task.Id, ReviewerType.Agent, Guid.NewGuid());
            run.Complete("checked", "check:exit0");
            seed.ReviewRuns.Add(run);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            userId = user.Id;
            runId = run.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result> RunDone()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new FinishWorkHandler(factory)
                    .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "done", "done-output", userId.ToString(), ReviewRunId: runId)));
            }
        }
        async Task<Result> RunFailed()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new FinishWorkHandler(factory)
                    .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "failed", "X", userId.ToString())));
            }
        }

        var results = await Task.WhenAll(RunDone(), RunFailed());

        // The status guard is ambiguous here: the done winner leaves no matching status for
        // the failed rival's stale read, and vice versa. The state revision picks exactly
        // one winner; the loser conflicts without side effects.
        Assert.Equal(1, results.Count(r => r.Success));
        var loser = results.Single(r => !r.Success);
        Assert.Equal(ErrorKind.Conflict, loser.Kind);
        var doneWon = results[0].Success;

        await using (var db = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                var parts = (row.Description ?? "").Split("\n\n");
                Assert.Equal(doneWon ? 0 : 1, parts.Count(p => p == "Failed: X"));
                Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
                if (doneWon)
                {
                    Assert.Equal(TaskItemStatus.Done, row.Status);
                    Assert.Equal("done-output", row.ReviewNotes);
                }
                else
                {
                    Assert.Equal(TaskItemStatus.Todo, row.Status);
                }
            }
        }

        // The loser retries with a fresh read and lands.
        var retry = doneWon ? RunFailed() : RunDone();
        Assert.True((await retry).Success);

        await using (var db = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                var parts = (row.Description ?? "").Split("\n\n");
                Assert.Equal(1, parts.Count(p => p == "Failed: X"));
                var comments = await db.TaskComments.Where(c => c.TaskId == taskId).ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, comments.Count);
                Assert.Contains(comments, c => c.Content.Contains("done"));
                Assert.Contains(comments, c => c.Content.Contains("failed"));
                if (doneWon)
                {
                    // The failed retry reopens the Done task to Todo (existing semantics).
                    Assert.Equal(TaskItemStatus.Todo, row.Status);
                }
                else
                {
                    // The done retry completes the task; the failure note stays as history.
                    Assert.Equal(TaskItemStatus.Done, row.Status);
                    Assert.Equal("done-output", row.ReviewNotes);
                }
            }
        }
    }

    [Fact]
    public async Task ConcurrentFinishWorkDoneVsFailed_FromInProgress_ExactlyOneWinner_RetryLands()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId, runId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("FW Mix Prog Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("FW Mix Prog Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-mix-prog-user", "fw-mix-prog-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("fw mix prog race", project.Id);
            task.TransitionTo(TaskItemStatus.InProgress);
            task.SetReviewNotes("seed notes");
            seed.Tasks.Add(task);
            var run = ReviewRun.Start(project.Id, task.Id, ReviewerType.Agent, Guid.NewGuid());
            run.Complete("checked", "check:exit0");
            seed.ReviewRuns.Add(run);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            userId = user.Id;
            runId = run.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result> RunDone()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new FinishWorkHandler(factory)
                    .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "done", "done-output", userId.ToString(), ReviewRunId: runId)));
            }
        }
        async Task<Result> RunFailed()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new FinishWorkHandler(factory)
                    .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "failed", "X", userId.ToString())));
            }
        }

        var results = await Task.WhenAll(RunDone(), RunFailed());

        Assert.Equal(1, results.Count(r => r.Success));
        var loser = results.Single(r => !r.Success);
        Assert.Equal(ErrorKind.Conflict, loser.Kind);
        var doneWon = results[0].Success;

        await using (var db = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                var parts = (row.Description ?? "").Split("\n\n");
                Assert.Equal(doneWon ? 0 : 1, parts.Count(p => p == "Failed: X"));
                Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
                if (doneWon)
                {
                    Assert.Equal(TaskItemStatus.Done, row.Status);
                    Assert.Equal("done-output", row.ReviewNotes);
                }
                else
                {
                    Assert.Equal(TaskItemStatus.Todo, row.Status);
                }
            }
        }

        var retry = doneWon ? RunFailed() : RunDone();
        Assert.True((await retry).Success);

        await using (var db = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                var parts = (row.Description ?? "").Split("\n\n");
                Assert.Equal(1, parts.Count(p => p == "Failed: X"));
                var comments = await db.TaskComments.Where(c => c.TaskId == taskId).ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, comments.Count);
                Assert.Contains(comments, c => c.Content.Contains("done"));
                Assert.Contains(comments, c => c.Content.Contains("failed"));
                if (doneWon)
                {
                    Assert.Equal(TaskItemStatus.Todo, row.Status);
                }
                else
                {
                    Assert.Equal(TaskItemStatus.Done, row.Status);
                    Assert.Equal("done-output", row.ReviewNotes);
                }
            }
        }
    }

    [Fact]
    public async Task ConcurrentFinishWorkFailed_SameOutput_BothOk_SingleNoteSingleComment()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("FW Same Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("FW Same Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-same-user", "fw-same-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("fw same race", project.Id);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            userId = user.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result> Run()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new FinishWorkHandler(factory)
                    .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "failed", "X", userId.ToString())));
            }
        }

        // The loser's guarded update misses the bumped revision, but the re-read shows the
        // report's effect is already in place — Todo plus the exact note — so it is an
        // idempotent Ok. Neither the note nor the comment may be duplicated.
        var results = await Task.WhenAll(Run(), Run());
        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using (var db = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                Assert.Equal(TaskItemStatus.Todo, row.Status);
                Assert.Equal(1, (row.Description ?? "").Split("\n\n").Count(p => p == "Failed: X"));
                Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
            }
        }

        // A sequential replay stays suppressed.
        Assert.True((await Run()).Success);
        await using (var db = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                Assert.Equal(1, (row.Description ?? "").Split("\n\n").Count(p => p == "Failed: X"));
                Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
            }
        }
    }

    [Fact]
    public async Task ConcurrentReviewVerdictVsFailed_FromReviewing_NoStaleResurrection()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("Verdict Failed Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("Verdict Failed Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("verdict-failed-user", "verdict-failed-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("verdict failed race", project.Id);
            task.StartPipeline();
            task.EnterExecuting();
            task.EnterReview();
            seed.Tasks.Add(task);
            seed.PipelineSessions.Add(PipelineSession.Create(task.Id, project.Id, PipelineRole.Review, "review prompt"));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            userId = user.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result<PipelineStateDto>> RunVerdict()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new RecordReviewVerdictHandler(factory)
                    .HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(taskId, ReviewVerdictKind.Approve, "looks good")));
            }
        }
        async Task<Result> RunFailed()
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new FinishWorkHandler(factory)
                    .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "failed", "X", userId.ToString())));
            }
        }

        var verdictTask = RunVerdict();
        var failedTask = RunFailed();
        var verdict = await verdictTask;
        var failed = await failedTask;

        // The failed report always lands: an approve does not move the status, so the
        // failed guard still matches. The single verdict either wins its stage claim or
        // loses at it — a stale in-memory status can never be written back.
        Assert.True(failed.Success, failed.Error);
        if (!verdict.Success)
            Assert.Equal("Review state changed while recording verdict; retry.", verdict.Error);

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.Todo, row.Status);
            Assert.Equal(1, (row.Description ?? "").Split("\n\n").Count(p => p == "Failed: X"));
            Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
            if (verdict.Success)
            {
                Assert.Equal(TaskPipelineStage.Approved, row.PipelineStage);
                Assert.Equal(1, await db.ReviewVerdicts.CountAsync(v => v.TaskId == taskId, TestContext.Current.CancellationToken));
                Assert.Equal(1, await db.ReviewRuns.CountAsync(r => r.TaskId == taskId, TestContext.Current.CancellationToken));
                var session = await db.PipelineSessions.FirstAsync(s => s.TaskId == taskId && s.Role == PipelineRole.Review, TestContext.Current.CancellationToken);
                Assert.Equal(SessionStatus.Closed, session.Status);
            }
            else
            {
                Assert.Equal(TaskPipelineStage.Reviewing, row.PipelineStage);
                Assert.Equal(0, await db.ReviewVerdicts.CountAsync(v => v.TaskId == taskId, TestContext.Current.CancellationToken));
                Assert.Equal(0, await db.ReviewRuns.CountAsync(r => r.TaskId == taskId, TestContext.Current.CancellationToken));
                var session = await db.PipelineSessions.FirstAsync(s => s.TaskId == taskId && s.Role == PipelineRole.Review, TestContext.Current.CancellationToken);
                Assert.Equal(SessionStatus.Ready, session.Status);
            }
        }
    }

    [Fact]
    public async Task ConcurrentFinishWorkSkipped_DifferentOutputs_BothNotesLand()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("FW Skip Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("FW Skip Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-skip-user", "fw-skip-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("fw skip race", project.Id);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            userId = user.Id;
        }

        var factory = _postgres.CreateFactory();
        async Task<Result> Run(string output)
        {
            using (TenantScope.EnterUnscoped())
            {
                return await new FinishWorkHandler(factory)
                    .HandleAsync(new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "skipped", output, userId.ToString())));
            }
        }

        // No status transition, but the concurrent note appends must both land.
        var results = await Task.WhenAll(Run("S1"), Run("S2"));
        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using var db = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.Todo, row.Status);
            var parts = (row.Description ?? "").Split("\n\n");
            Assert.Equal(1, parts.Count(p => p == "Note: S1"));
            Assert.Equal(1, parts.Count(p => p == "Note: S2"));
        }
    }

    [Fact]
    public async Task FinishWorkDone_CommentInsertFailure_RollsBackState_RetryLandsOnce()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId, proofId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("FW inj Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("FW inj Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-inj-done-user", "fw-inj-done-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("fw inj done", project.Id);
            seed.Tasks.Add(task);
            var run = ReviewRun.Start(project.Id, task.Id, ReviewerType.Agent, Guid.NewGuid());
            run.Complete("checked", "check:exit0");
            seed.ReviewRuns.Add(run);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            userId = user.Id;
            proofId = run.Id;

            // Fault injection at the database level: refuse the comment insert for
            // marked payloads. Created on the disposable fixture database only.
            await seed.Database.ExecuteSqlRawAsync(@"
                CREATE FUNCTION beacon_finish_inject_fail() RETURNS trigger AS $$
                BEGIN
                    IF NEW.""Content"" LIKE '%injectFail%' THEN
                        RAISE EXCEPTION 'injected comment insert failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER beacon_finish_inject_fail
                BEFORE INSERT ON ""TaskComments""
                FOR EACH ROW EXECUTE FUNCTION beacon_finish_inject_fail();", TestContext.Current.CancellationToken);
        }

        var factory = _postgres.CreateFactory();
        try
        {
            // The guarded state write runs, then the comment insert is refused.
            using (TenantScope.EnterUnscoped())
            {
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    new FinishWorkHandler(factory).HandleAsync(
                        new FinishWorkCommand(new FinishWorkRequest(
                            taskId.ToString(), "done", "injectFail", userId.ToString(), ReviewRunId: proofId)), TestContext.Current.CancellationToken));
            }

            // The state write rolled back with the failed comment: the row is exactly
            // as before, and no comment exists.
            await using (var db = _postgres.CreateAdminContext())
            {
                using (TenantScope.EnterUnscoped())
                {
                    var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                    Assert.Equal(TaskItemStatus.Todo, row.Status);
                    Assert.Null(row.ReviewNotes);
                    Assert.Null(row.CompletedAt);
                    Assert.Equal(0, row.StateRevision);
                    Assert.Equal(0, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
                }
            }

            // The same request after the fault is gone lands exactly once.
            using (TenantScope.EnterUnscoped())
            {
                var retry = await new FinishWorkHandler(factory).HandleAsync(
                    new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "done", "clean retry", userId.ToString(), ReviewRunId: proofId)), TestContext.Current.CancellationToken);
                Assert.True(retry.Success, retry.Error);
            }
            await using (var db = _postgres.CreateAdminContext())
            {
                using (TenantScope.EnterUnscoped())
                {
                    var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                    Assert.Equal(TaskItemStatus.Done, row.Status);
                    Assert.Equal("clean retry", row.ReviewNotes);
                    Assert.Equal(1, row.StateRevision);
                    Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
                }
            }
        }
        finally
        {
            await using var cleanup = _postgres.CreateAdminContext();
            await cleanup.Database.ExecuteSqlRawAsync(@"
                DROP TRIGGER IF EXISTS beacon_finish_inject_fail ON ""TaskComments"";
                DROP FUNCTION IF EXISTS beacon_finish_inject_fail();", TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FinishWorkFailed_CommentInsertFailure_RollsBackNote_RetryLandsOnce()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("FW inj Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("FW inj Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-inj-failed-user", "fw-inj-failed-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("fw inj failed", project.Id);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            userId = user.Id;

            await seed.Database.ExecuteSqlRawAsync(@"
                CREATE FUNCTION beacon_finish_inject_fail() RETURNS trigger AS $$
                BEGIN
                    IF NEW.""Content"" LIKE '%injectFail%' THEN
                        RAISE EXCEPTION 'injected comment insert failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER beacon_finish_inject_fail
                BEFORE INSERT ON ""TaskComments""
                FOR EACH ROW EXECUTE FUNCTION beacon_finish_inject_fail();", TestContext.Current.CancellationToken);
        }

        var factory = _postgres.CreateFactory();
        try
        {
            // The note append and status write run, then the comment insert is refused.
            using (TenantScope.EnterUnscoped())
            {
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    new FinishWorkHandler(factory).HandleAsync(
                        new FinishWorkCommand(new FinishWorkRequest(
                            taskId.ToString(), "failed", "injectFail", userId.ToString())), TestContext.Current.CancellationToken));
            }

            // The note append rolled back with the failed comment: the row is unchanged.
            await using (var db = _postgres.CreateAdminContext())
            {
                using (TenantScope.EnterUnscoped())
                {
                    var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                    Assert.Equal(TaskItemStatus.Todo, row.Status);
                    Assert.Null(row.Description);
                    Assert.Equal(0, row.StateRevision);
                    Assert.Equal(0, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
                }
            }

            // The same request after the fault is gone lands the note once, with one comment.
            using (TenantScope.EnterUnscoped())
            {
                var retry = await new FinishWorkHandler(factory).HandleAsync(
                    new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "failed", "clean retry", userId.ToString())), TestContext.Current.CancellationToken);
                Assert.True(retry.Success, retry.Error);
            }
            await using (var db = _postgres.CreateAdminContext())
            {
                using (TenantScope.EnterUnscoped())
                {
                    var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                    Assert.Equal(TaskItemStatus.Todo, row.Status);
                    Assert.Equal("Failed: clean retry", row.Description);
                    Assert.Equal(1, row.StateRevision);
                    Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
                }
            }
        }
        finally
        {
            await using var cleanup = _postgres.CreateAdminContext();
            await cleanup.Database.ExecuteSqlRawAsync(@"
                DROP TRIGGER IF EXISTS beacon_finish_inject_fail ON ""TaskComments"";
                DROP FUNCTION IF EXISTS beacon_finish_inject_fail();", TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FinishWorkSkipped_CommentInsertFailure_RollsBackNote_RetryLandsOnce()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("FW inj Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var project = Project.Create("FW inj Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-inj-skipped-user", "fw-inj-skipped-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("fw inj skipped", project.Id);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            taskId = task.Id;
            userId = user.Id;

            await seed.Database.ExecuteSqlRawAsync(@"
                CREATE FUNCTION beacon_finish_inject_fail() RETURNS trigger AS $$
                BEGIN
                    IF NEW.""Content"" LIKE '%injectFail%' THEN
                        RAISE EXCEPTION 'injected comment insert failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER beacon_finish_inject_fail
                BEFORE INSERT ON ""TaskComments""
                FOR EACH ROW EXECUTE FUNCTION beacon_finish_inject_fail();", TestContext.Current.CancellationToken);
        }

        var factory = _postgres.CreateFactory();
        try
        {
            // The note append runs, then the comment insert is refused.
            using (TenantScope.EnterUnscoped())
            {
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    new FinishWorkHandler(factory).HandleAsync(
                        new FinishWorkCommand(new FinishWorkRequest(
                            taskId.ToString(), "skipped", "injectFail", userId.ToString())), TestContext.Current.CancellationToken));
            }

            // The note append rolled back with the failed comment; status never moves for skipped.
            await using (var db = _postgres.CreateAdminContext())
            {
                using (TenantScope.EnterUnscoped())
                {
                    var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                    Assert.Equal(TaskItemStatus.Todo, row.Status);
                    Assert.Null(row.Description);
                    Assert.Equal(0, row.StateRevision);
                    Assert.Equal(0, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
                }
            }

            // The same request after the fault is gone lands the note once, with one comment.
            using (TenantScope.EnterUnscoped())
            {
                var retry = await new FinishWorkHandler(factory).HandleAsync(
                    new FinishWorkCommand(new FinishWorkRequest(
                        taskId.ToString(), "skipped", "clean retry", userId.ToString())), TestContext.Current.CancellationToken);
                Assert.True(retry.Success, retry.Error);
            }
            await using (var db = _postgres.CreateAdminContext())
            {
                using (TenantScope.EnterUnscoped())
                {
                    var row = await db.Tasks.FirstAsync(t => t.Id == taskId, TestContext.Current.CancellationToken);
                    Assert.Equal(TaskItemStatus.Todo, row.Status);
                    Assert.Equal("Note: clean retry", row.Description);
                    Assert.Equal(0, row.StateRevision);
                    Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId, TestContext.Current.CancellationToken));
                }
            }
        }
        finally
        {
            await using var cleanup = _postgres.CreateAdminContext();
            await cleanup.Database.ExecuteSqlRawAsync(@"
                DROP TRIGGER IF EXISTS beacon_finish_inject_fail ON ""TaskComments"";
                DROP FUNCTION IF EXISTS beacon_finish_inject_fail();", TestContext.Current.CancellationToken);
        }
    }

    private async Task<(Guid ProjectId, Guid TaskId)> SeedTaskAsync(string title)
    {
        await using var seed = _postgres.CreateAdminContext();
        var org = Org.Create("Race Org", null);
        seed.Orgs.Add(org);
        await seed.SaveChangesAsync();
        var project = Project.Create("Race Project", null, org.Id);
        seed.Projects.Add(project);
        var task = TaskItem.Create(title, project.Id);
        seed.Tasks.Add(task);
        await seed.SaveChangesAsync();
        return (project.Id, task.Id);
    }

    /// <summary>Seeds an online chat device with an attached runtime so the chat handlers can run.</summary>
    private async Task<(Guid ProjectId, Guid DeviceId, Guid UserId, Guid SessionId)> SeedChatAsync()
    {
        await using var seed = _postgres.CreateAdminContext();
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("chat-" + Guid.NewGuid().ToString("N")[..8], $"chat-{Guid.NewGuid():N}@beacon.local", "hash");
            var org = Org.Create("Chat Race Org");
            seed.Users.Add(user);
            seed.Orgs.Add(org);
            var project = Project.Create("Chat Race Project", null, org.Id);
            seed.Projects.Add(project);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            var device = DaemonDevice.Create("chat-device", user.Id,
                "fp-" + Guid.NewGuid().ToString("N"), "hash-" + Guid.NewGuid().ToString("N"),
                "bcd_" + Guid.NewGuid().ToString("N")[..4]);
            seed.DaemonDevices.Add(device);
            var session = ChatSession.Create(project.Id, device.Id, "Chat Race", @"A:\work");
            session.SetExternalSessionId("oc_" + Guid.NewGuid().ToString("N"));
            seed.ChatSessions.Add(session);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            var factory = _postgres.CreateFactory();
            var beat = await new HeartbeatDeviceHandler(factory)
                .HandleAsync(new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(device.Id, "{}", "{}")), TestContext.Current.CancellationToken);
            Assert.True(beat.Success, beat.Error);
            var attach = await new AttachRuntimeHandler(factory)
                .HandleAsync(new AttachRuntimeCommand(new AttachRuntimeRequest(project.Id, device.Id, user.Id, @"A:\work")), TestContext.Current.CancellationToken);
            Assert.True(attach.Success, attach.Error);
            return (project.Id, device.Id, user.Id, session.Id);
        }
    }

    [Fact]
    public async Task ChatPart_SortOrderIndex_IsUnique()
    {
        _postgres.RequirePostgres();
        var (projectId, deviceId, _, sessionId) = await SeedChatAsync();
        var factory = _postgres.CreateFactory();
        using (TenantScope.EnterUnscoped())
        {
            var appended = await new AppendChatPartHandler(factory, new ChatEventSignal())
                .HandleAsync(new AppendChatPartCommand(new AppendChatPartRequest(sessionId, deviceId, "assistant", "text", "first", "pa")), TestContext.Current.CancellationToken);
            Assert.True(appended.Success, appended.Error);

            // A second part at the same sort order must be rejected by the index, not stored.
            await using var db = _postgres.CreateAdminContext();
            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
                await db.Database.ExecuteSqlRawAsync(
                    @"INSERT INTO ""ChatParts"" (""Id"", ""SessionId"", ""ProjectId"", ""Role"", ""Kind"", ""Body"", ""SortOrder"", ""CreatedAt"") " +
                    $"VALUES ('{Guid.NewGuid()}', '{sessionId}', '{projectId}', 'assistant', 'text', 'duplicate', {appended.Value!.SortOrder}, NOW())", TestContext.Current.CancellationToken));
            Assert.Equal("23505", ex.SqlState);
        }
    }

    [Fact]
    public async Task ConcurrentSendAndAppend_NoLostParts_UniqueSortOrders()
    {
        _postgres.RequirePostgres();
        var (_, deviceId, userId, sessionId) = await SeedChatAsync();
        var factory = _postgres.CreateFactory();
        var ct = TestContext.Current.CancellationToken;

        using (TenantScope.EnterUnscoped())
        {
            // One send and seven appends start together: every writer recomputes the next
            // sort order, and the unique index plus retry keeps all eight parts distinct.
            var sendTask = new SendChatPromptHandler(factory, new ChatEventSignal())
                .HandleAsync(new SendChatPromptCommand(new SendChatPromptRequest(sessionId, userId, "storm", null)), ct);
            var appendTasks = Enumerable.Range(1, 7)
                .Select(i => new AppendChatPartHandler(factory, new ChatEventSignal())
                    .HandleAsync(new AppendChatPartCommand(
                        new AppendChatPartRequest(sessionId, deviceId, "assistant", "text", "part-" + i, "pa-" + i)), ct))
                .ToArray();

            var sent = await sendTask;
            Assert.True(sent.Success, sent.Error);
            var appended = await Task.WhenAll(appendTasks);
            foreach (var result in appended)
                Assert.True(result.Success, result.Error);

            await using var db = _postgres.CreateAdminContext();
            var parts = await db.ChatParts.Where(p => p.SessionId == sessionId).ToListAsync(ct);
            Assert.Equal(8, parts.Count);
            Assert.Equal(8, parts.Select(p => p.SortOrder).Distinct().Count());
        }
    }

    [Fact]
    public async Task SendVsArchive_Race_NeverArchivesStreamingTurn()
    {
        _postgres.RequirePostgres();
        // "Exactly one wins" is not an invariant: an archive that loads the session after the
        // send commits aborts first and then archives, so both calls can legitimately succeed.
        for (var round = 0; round < 5; round++)
        {
            var (projectId, deviceId, userId, sessionId) = await SeedChatAsync();
            var factory = _postgres.CreateFactory();
            var ct = TestContext.Current.CancellationToken;

            using (TenantScope.EnterUnscoped())
            {
                var sendTask = new SendChatPromptHandler(factory, new ChatEventSignal())
                    .HandleAsync(new SendChatPromptCommand(new SendChatPromptRequest(sessionId, userId, "race", null)), ct);
                var archiveTask = new ArchiveChatSessionHandler(factory,
                        new AbortChatHandler(factory, new EnqueueCommandHandler(factory), new ChatEventSignal()),
                        new ChatEventSignal())
                    .HandleAsync(new ArchiveChatSessionCommand(new ArchiveChatSessionRequest(sessionId, userId)), ct);
                var sent = await sendTask;
                var archived = await archiveTask;

                await using var db = _postgres.CreateAdminContext();
                var status = await db.ChatSessions.AsNoTracking()
                    .Where(s => s.Id == sessionId).Select(s => s.Status).FirstAsync(ct);
                var archivedAt = await db.ChatSessions.AsNoTracking()
                    .Where(s => s.Id == sessionId).Select(s => s.ArchivedAt).FirstAsync(ct);
                var partCount = await db.ChatParts.Where(p => p.SessionId == sessionId).CountAsync(ct);
                var promptCount = await db.WorkstationCommands
                    .Where(c => c.ProjectId == projectId && c.Kind == WorkstationCommandKind.ChatPrompt)
                    .CountAsync(ct);

                Assert.False(status == ChatSessionStatus.Streaming && archivedAt is not null,
                    "round " + round + ": the session was archived while streaming");
                if (sent.Success && !archived.Success)
                {
                    Assert.Equal(ChatSessionStatus.Streaming, status);
                    Assert.Equal(1, partCount);
                    Assert.Equal(1, promptCount);
                }
                else if (!sent.Success)
                {
                    Assert.True(archivedAt is not null, "round " + round + ": send lost but the session was not archived");
                    Assert.Equal(ChatSessionStatus.Idle, status);
                    Assert.Equal(0, partCount);
                    Assert.Equal(0, promptCount);
                }
                else
                {
                    Assert.True(archivedAt is not null, "round " + round + ": send won but the session was not archived");
                    Assert.Equal(1, partCount);
                    Assert.True(promptCount >= 1);
                }
            }
        }
    }
}