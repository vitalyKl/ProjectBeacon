namespace ProjectBeacon.Application.Tests;

using Application.Common;
using Application.Devices;
using Application.Projects;
using Domain.Entities.Audit;
using Domain.Entities.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Entities.Evals;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class DeviceRemoteLandingReconciliationHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public DeviceRemoteLandingReconciliationHandlerTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private BeaconDbFactory Factory() => HandlerSqlite.Factory(_connection);

    [Fact]
    public async Task ReconcilesExactMergedPullRequestAndClosesPinnedApprovedTask()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: true);
        using (TenantScope.EnterUnscoped())
        {
            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var request = Request(setup);

            var result = await handler.HandleAsync(setup.DeviceId, request, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            Assert.Equal("Done", result.Value!.TaskStatus);
            Assert.Equal("Closed", result.Value.PipelineStage);
            Assert.False(result.Value.AlreadyRecorded);

            await using var verify = Factory().CreateDbContext();
            var task = await verify.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == setup.TaskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.Done, task.Status);
            Assert.Equal(TaskPipelineStage.Closed, task.PipelineStage);
            Assert.Contains("GitHub PR #42", task.ReviewNotes, StringComparison.Ordinal);
            Assert.Contains(setup.SourceSha, task.ReviewNotes, StringComparison.Ordinal);

            var proof = await verify.RemoteLandingProofs.AsNoTracking()
                .SingleAsync(item => item.TaskId == setup.TaskId, TestContext.Current.CancellationToken);
            Assert.Equal(setup.DeviceId, proof.DeviceId);
            Assert.Equal(setup.ReviewRunId, proof.ReviewRunId);
            Assert.Equal(setup.SourceSha, proof.SourceSha);
            Assert.Equal(setup.MergeSha, proof.MergeCommitSha);
            Assert.Equal(setup.RemoteBaseSha, proof.RemoteBaseSha);
            Assert.Equal("acme/project-beacon", proof.GitHubRepository);

            var audit = await verify.AuditEvents.AsNoTracking()
                .SingleAsync(item => item.Action == "task.remote_landing.reconciled", TestContext.Current.CancellationToken);
            Assert.Equal(AuditActorKind.Device, audit.ActorKind);
            Assert.Equal(setup.DeviceId, audit.ActorDeviceId);
        }
    }

    [Fact]
    public async Task ReconciliationIsIdempotentForTheSamePullRequestAndProof()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: true);
        using (TenantScope.EnterUnscoped())
        {
            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var request = Request(setup);

            var first = await handler.HandleAsync(setup.DeviceId, request, TestContext.Current.CancellationToken);
            var second = await handler.HandleAsync(setup.DeviceId, request, TestContext.Current.CancellationToken);

            Assert.True(first.Success, first.Error);
            Assert.True(second.Success, second.Error);
            Assert.True(second.Value!.AlreadyRecorded);
            Assert.Equal(first.Value!.ProofId, second.Value.ProofId);

            await using var verify = Factory().CreateDbContext();
            Assert.Equal(1, await verify.RemoteLandingProofs.CountAsync(
                proof => proof.TaskId == setup.TaskId, TestContext.Current.CancellationToken));
            Assert.Equal(1, await verify.AuditEvents.CountAsync(
                audit => audit.Action == "task.remote_landing.reconciled", TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task RejectsSourceShaThatDiffersFromLatestReviewProof()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: true);
        using (TenantScope.EnterUnscoped())
        {
            var request = Request(setup) with { SourceSha = new string('f', 40) };
            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());

            var result = await handler.HandleAsync(setup.DeviceId, request, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(ErrorKind.Conflict, result.Kind);
            await using var verify = Factory().CreateDbContext();
            var task = await verify.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == setup.TaskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.InProgress, task.Status);
            Assert.Equal(TaskPipelineStage.Approved, task.PipelineStage);
            Assert.Empty(await verify.RemoteLandingProofs.AsNoTracking()
                .Where(proof => proof.TaskId == setup.TaskId)
                .ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task RejectsDifferentWorkstationAndAutoSyncDisabled()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: true);
        Guid otherDeviceId;
        using (TenantScope.EnterUnscoped())
        {
            var other = await new CreateDeviceHandler(Factory()).HandleAsync(
                new CreateDeviceCommand(new CreateDeviceRequest("other", "fp-other", setup.UserId)),
                TestContext.Current.CancellationToken);
            Assert.True(other.Success, other.Error);
            otherDeviceId = other.Value!.Id;

            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var wrongDevice = await handler.HandleAsync(
                otherDeviceId, Request(setup), TestContext.Current.CancellationToken);
            Assert.False(wrongDevice.Success);
            Assert.Equal(ErrorKind.Forbidden, wrongDevice.Kind);
        }

        var disabled = await CreateApprovedTaskAsync(autoSyncEnabled: false);
        using (TenantScope.EnterUnscoped())
        {
            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var result = await handler.HandleAsync(
                disabled.DeviceId, Request(disabled), TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Equal(ErrorKind.Forbidden, result.Kind);
        }
    }

    [Fact]
    public async Task DoesNotCloseTaskWhilePipelineSessionIsActive()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: true, activeSession: true);
        using (TenantScope.EnterUnscoped())
        {
            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var result = await handler.HandleAsync(
                setup.DeviceId, Request(setup), TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(ErrorKind.Conflict, result.Kind);
            await using var verify = Factory().CreateDbContext();
            var task = await verify.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == setup.TaskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.InProgress, task.Status);
            Assert.Equal(TaskPipelineStage.Approved, task.PipelineStage);
            Assert.Empty(await verify.RemoteLandingProofs.AsNoTracking()
                .Where(proof => proof.TaskId == setup.TaskId)
                .ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    private async Task<SetupData> CreateApprovedTaskAsync(bool autoSyncEnabled, bool activeSession = false)
    {
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("remote-landing", $"remote-{Guid.NewGuid():N}@beacon.local", "hash");
            var org = Org.Create("Remote landing");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var project = Project.Create("GitHub remote", null, org.Id);
            project.SetGitPolicyJson(ProjectGitPolicyCatalog.Serialize(
                0, [], 200, 20_000, "Off", autoSyncEnabled: autoSyncEnabled));
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var created = await new CreateDeviceHandler(Factory()).HandleAsync(
                new CreateDeviceCommand(new CreateDeviceRequest("pinned", "fp-pinned", user.Id)),
                TestContext.Current.CancellationToken);
            Assert.True(created.Success, created.Error);
            var deviceId = created.Value!.Id;
            var device = await _db.DaemonDevices.SingleAsync(
                item => item.Id == deviceId, TestContext.Current.CancellationToken);
            device.Heartbeat(
                """{"capabilities":["git-sync-v1","git-auto-sync-v1"]}""", "{}");
            _db.ProjectRuntimes.Add(ProjectRuntime.Create(project.Id, deviceId, "/workspace/project"));
            var task = TaskItem.Create("Merged via PR", project.Id, description: "Change under review.");
            task.PinPipelineDevice(deviceId);
            task.StartPipeline();
            task.EnterExecuting();
            task.EnterReview();
            task.SetApproved();
            _db.Tasks.Add(task);

            var reviewRunId = Guid.NewGuid();
            var review = ReviewRun.Start(
                project.Id, task.Id, ReviewerType.Agent, targetRunId: reviewRunId);
            review.SetCheckCommandHash(new string('a', 64));
            var sourceSha = new string('1', 40);
            var treeSha = new string('2', 40);
            review.CompleteCheck(
                "CI and repository checks passed.", "review-artifact",
                sourceSha, treeSha, deviceId, "main");
            _db.ReviewRuns.Add(review);
            _db.ReviewVerdicts.Add(ReviewVerdict.Create(
                task.Id, project.Id, ReviewVerdictKind.Approve, "Reviewed and approved."));

            if (activeSession)
            {
                _db.PipelineSessions.Add(PipelineSession.Create(
                    task.Id, project.Id, PipelineRole.Actor, "Active Actor session."));
            }

            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return new SetupData(
                user.Id, project.Id, task.Id, deviceId, review.Id,
                sourceSha, new string('3', 40), new string('4', 40), org.Id);
        }
    }

    private static DeviceRemoteLandingReconciliationRequest Request(SetupData setup)
    {
        var now = DateTime.UtcNow;
        return new DeviceRemoteLandingReconciliationRequest(
            setup.ProjectId,
            setup.TaskId,
            setup.ReviewRunId,
            "acme/project-beacon",
            42,
            "https://github.com/acme/project-beacon/pull/42",
            $"beacon/task/{setup.TaskId:N}",
            "main",
            setup.SourceSha,
            setup.MergeSha,
            setup.RemoteBaseSha,
            now.AddMinutes(-4),
            now);
    }

    private sealed record SetupData(
        Guid UserId,
        Guid ProjectId,
        Guid TaskId,
        Guid DeviceId,
        Guid ReviewRunId,
        string SourceSha,
        string MergeSha,
        string RemoteBaseSha,
        Guid OrgId);
}
