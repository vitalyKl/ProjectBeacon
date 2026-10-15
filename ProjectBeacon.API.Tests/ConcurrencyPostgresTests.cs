namespace ProjectBeacon.API.Tests;

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
