namespace ProjectBeacon.API.Tests;

using System.Text.Json.Nodes;
using Application.Authorization;
using Application.Common;
using Application.Devices;
using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

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
            Assert.Equal(1, await db.PipelineSessions.CountAsync(s => s.TaskId == taskId));
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId);
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
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId);
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
            await seed.SaveChangesAsync();
            var project = Project.Create("Verdict Race Project", null, org.Id);
            seed.Projects.Add(project);
            var task = TaskItem.Create("verdict race", project.Id);
            task.StartPipeline();
            task.EnterExecuting();
            task.EnterReview();
            seed.Tasks.Add(task);
            seed.PipelineSessions.Add(PipelineSession.Create(task.Id, project.Id, PipelineRole.Review, "review prompt"));
            await seed.SaveChangesAsync();
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
            Assert.Equal(1, await db.ReviewVerdicts.CountAsync(v => v.TaskId == taskId));
            Assert.Equal(1, await db.ReviewRuns.CountAsync(r => r.TaskId == taskId));
            var session = await db.PipelineSessions.FirstAsync(s => s.TaskId == taskId && s.Role == PipelineRole.Review);
            Assert.Equal(SessionStatus.Closed, session.Status);
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId);
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
            await seed.SaveChangesAsync();
            var project = Project.Create("Subtask Race Project", null, org.Id);
            seed.Projects.Add(project);
            var task = TaskItem.Create("subtask race", project.Id);
            task.StartPipeline();
            task.EnterExecuting();
            seed.Tasks.Add(task);
            var sub = Subtask.Create("do the work", task.Id, project.Id);
            sub.Start();
            seed.Subtasks.Add(sub);
            await seed.SaveChangesAsync();
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
            var row = await db.Subtasks.FirstAsync(s => s.Id == subtaskId);
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
            await seed.SaveChangesAsync();
            var device = DaemonDevice.Create("completion device", user.Id, "fp-" + Guid.NewGuid().ToString("N"), "hash-" + Guid.NewGuid().ToString("N"), "bcd_completion");
            seed.DaemonDevices.Add(device);
            var command = WorkstationCommand.Create(device.Id, WorkstationCommandKind.ListDir, "{}");
            seed.WorkstationCommands.Add(command);
            await seed.SaveChangesAsync();
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
            var row = await db.WorkstationCommands.FirstAsync(c => c.Id == commandId);
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
            await seed.SaveChangesAsync();
            var org = Org.Create("Check Race Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync();
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
            await seed.SaveChangesAsync();
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
                .CountAsync(c => c.DeviceId == deviceId && c.Kind == WorkstationCommandKind.RunReviewCheck));
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
            await seed.SaveChangesAsync();
            var device = DaemonDevice.Create("save device", user.Id, "fp-" + Guid.NewGuid().ToString("N"), "hash-" + Guid.NewGuid().ToString("N"), "bcd_save");
            device.Heartbeat();
            seed.DaemonDevices.Add(device);
            await seed.SaveChangesAsync();
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
            var row = await db.DaemonDevices.FirstAsync(d => d.Id == deviceId);
            Assert.Equal(1, row.DesiredRevision);

            // Only the winner enqueued a command, and the stored JSON pairs with its payload.
            Assert.Equal(1, await db.WorkstationCommands.CountAsync(c => c.DeviceId == deviceId && c.Kind == WorkstationCommandKind.SaveWorkstation));
            winningPayload.Remove("revision");
            Assert.True(JsonNode.DeepEquals(winningPayload, JsonNode.Parse(row.DesiredWorkstationJson)));
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
            await seed.SaveChangesAsync();
            var device = DaemonDevice.Create("bump device", user.Id, "fp-" + Guid.NewGuid().ToString("N"), "hash-" + Guid.NewGuid().ToString("N"), "bcd_bump");
            seed.DaemonDevices.Add(device);
            await seed.SaveChangesAsync();
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
            var row = await db.DaemonDevices.FirstAsync(d => d.Id == deviceId);
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
            await seed.SaveChangesAsync();
            var project = Project.Create("Actor Race Project", null, org.Id);
            seed.Projects.Add(project);
            var task = TaskItem.Create("actor race", project.Id);
            task.StartPipeline();
            task.EnterExecuting();
            seed.Tasks.Add(task);
            var sub = Subtask.Create("do the work", task.Id, project.Id);
            seed.Subtasks.Add(sub);
            await seed.SaveChangesAsync();
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
                .Where(s => s.TaskId == taskId && s.Role == PipelineRole.Actor).ToListAsync();
            Assert.Single(actorSessions);
            Assert.Equal(okIds[0], actorSessions[0].Id);
            var row = await db.Subtasks.FirstAsync(s => s.Id == subtaskId);
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
            await seed.SaveChangesAsync();
            var project = Project.Create("Done Race Project", null, org.Id);
            seed.Projects.Add(project);
            var task = TaskItem.Create("done race", project.Id);
            task.SetReviewNotes("reviewed");
            seed.Tasks.Add(task);
            var run = ReviewRun.Start(project.Id, task.Id, ReviewerType.Agent, Guid.NewGuid());
            run.Complete("checked", "check:exit0");
            seed.ReviewRuns.Add(run);
            await seed.SaveChangesAsync();
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
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId);
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
            await seed.SaveChangesAsync();
            var project = Project.Create("Mix Race Project", null, org.Id);
            seed.Projects.Add(project);
            var task = TaskItem.Create("mixed race", project.Id);
            task.SetReviewNotes("reviewed");
            task.TransitionTo(TaskItemStatus.InProgress);
            seed.Tasks.Add(task);
            var run = ReviewRun.Start(project.Id, task.Id, ReviewerType.Agent, Guid.NewGuid());
            run.Complete("checked", "check:exit0");
            seed.ReviewRuns.Add(run);
            await seed.SaveChangesAsync();
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
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId);
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
            await seed.SaveChangesAsync();
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
            await seed.SaveChangesAsync();
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
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId);
            Assert.Equal(TaskItemStatus.Done, row.Status);
            Assert.Contains(row.ReviewNotes, new[] { "notes-a", "notes-b" });
            Assert.Equal(1, await db.TaskComments.CountAsync(c => c.TaskId == taskId));
        }
    }

    [Fact]
    public async Task ConcurrentFinishWorkFailed_DifferentOutputs_BothNotesLand_ReplaySuppressed()
    {
        _postgres.RequirePostgres();
        Guid taskId, userId;
        await using (var seed = _postgres.CreateAdminContext())
        {
            var org = Org.Create("FW Fail Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync();
            var project = Project.Create("FW Fail Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-fail-user", "fw-fail-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("fw fail race", project.Id);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync();
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

        // Both reports read a Todo task, and a failed report keeps the task Todo, so both
        // guarded updates land. The note is appended atomically in SQL, so both notes
        // survive — no last-write-wins clobber.
        var results = await Task.WhenAll(Run("X"), Run("Y"));
        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using (var db = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var row = await db.Tasks.FirstAsync(t => t.Id == taskId);
                Assert.Equal(TaskItemStatus.Todo, row.Status);
                var parts = (row.Description ?? "").Split("\n\n");
                Assert.Equal(1, parts.Count(p => p == "Failed: X"));
                Assert.Equal(1, parts.Count(p => p == "Failed: Y"));
                Assert.Equal(2, await db.TaskComments.CountAsync(c => c.TaskId == taskId));
            }
        }

        // A sequential replay of either report is suppressed: no third note, no third comment.
        Assert.True((await Run("X")).Success);
        await using (var db = _postgres.CreateAdminContext())
        {
            using (TenantScope.EnterUnscoped())
            {
                var row = await db.Tasks.FirstAsync(t => t.Id == taskId);
                var after = (row.Description ?? "").Split("\n\n");
                Assert.Equal(1, after.Count(p => p == "Failed: X"));
                Assert.Equal(1, after.Count(p => p == "Failed: Y"));
                Assert.Equal(2, await db.TaskComments.CountAsync(c => c.TaskId == taskId));
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
            await seed.SaveChangesAsync();
            var project = Project.Create("FW Skip Project", null, org.Id);
            seed.Projects.Add(project);
            var user = User.Create("fw-skip-user", "fw-skip-user@beacon.local", "hash");
            seed.Users.Add(user);
            seed.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Admin));
            var task = TaskItem.Create("fw skip race", project.Id);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync();
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
            var row = await db.Tasks.FirstAsync(t => t.Id == taskId);
            Assert.Equal(TaskItemStatus.Todo, row.Status);
            var parts = (row.Description ?? "").Split("\n\n");
            Assert.Equal(1, parts.Count(p => p == "Note: S1"));
            Assert.Equal(1, parts.Count(p => p == "Note: S2"));
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
}
