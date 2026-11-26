namespace ProjectBeacon.Application.Tests;

using System.Security.Cryptography;
using System.Text;
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

    private const string ReviewCheckCommand = "dotnet test --no-build";
    private static string ReviewCheckCommandHash => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(ReviewCheckCommand))).ToLowerInvariant();

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
    public async Task DoesNotCloseTaskWhenBeaconLandingAttemptHasNotReachedLanded()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: true);
        using (TenantScope.EnterUnscoped())
        {
            var attempt = LandingAttempt.Start(
                setup.ProjectId, setup.TaskId, setup.ReviewRunId, setup.UserId, setup.DeviceId,
                "main", setup.SourceSha, new string('2', 40), ReviewCheckCommandHash,
                "Beacon-managed landing awaiting Execute.");
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var result = await handler.HandleAsync(
                setup.DeviceId, Request(setup) with { LandingAttemptId = attempt.Id }, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(ErrorKind.Conflict, result.Kind);
            Assert.Contains("verified Landed state", result.Error ?? string.Empty, StringComparison.Ordinal);
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
    public async Task ReconcilesWhenBeaconLandingAttemptIsLandedForTheExactMerge()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: false);
        using (TenantScope.EnterUnscoped())
        {
            var candidateTree = new string('5', 40);
            var postMergeHash = new string('7', 64);
            var attempt = LandingAttempt.Start(
                setup.ProjectId, setup.TaskId, setup.ReviewRunId, setup.UserId, setup.DeviceId,
                "main", setup.SourceSha, new string('2', 40), ReviewCheckCommandHash,
                "Beacon-managed landing with verified GitHub merge.");
            attempt.RecordPreview(
                "main", setup.RemoteBaseSha, candidateTree, new string('6', 64), 0, null);
            attempt.MarkExecutePending();
            attempt.RecordLanded(setup.MergeSha, candidateTree, 0, postMergeHash);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            // A later Project setting edit must not invalidate an already completed,
            // immutable post-merge check recorded by GitLand.
            var project = await _db.Projects.SingleAsync(
                item => item.Id == setup.ProjectId, TestContext.Current.CancellationToken);
            project.Update(reviewCheckCommand: "dotnet test");
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var missingProof = await handler.HandleAsync(
                setup.DeviceId,
                Request(setup) with
                {
                    LandingAttemptId = attempt.Id,
                    CandidateTreeSha = candidateTree,
                    PostMergeCheckExitCode = 0
                },
                TestContext.Current.CancellationToken);
            Assert.False(missingProof.Success);
            Assert.Equal(ErrorKind.Conflict, missingProof.Kind);
            Assert.Contains("post-merge evidence", missingProof.Error ?? string.Empty, StringComparison.Ordinal);

            var wrongTree = await handler.HandleAsync(
                setup.DeviceId,
                Request(setup) with
                {
                    LandingAttemptId = attempt.Id,
                    CandidateTreeSha = new string('4', 40),
                    PostMergeCheckExitCode = 0,
                    PostMergeCheckOutputHash = postMergeHash
                },
                TestContext.Current.CancellationToken);
            Assert.False(wrongTree.Success);
            Assert.Equal(ErrorKind.Conflict, wrongTree.Kind);

            var result = await handler.HandleAsync(
                setup.DeviceId,
                Request(setup) with
                {
                    LandingAttemptId = attempt.Id,
                    CandidateTreeSha = candidateTree,
                    PostMergeCheckExitCode = 0,
                    PostMergeCheckOutputHash = postMergeHash
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            Assert.Equal("Done", result.Value!.TaskStatus);
            Assert.Equal("Closed", result.Value.PipelineStage);
            await using var verify = Factory().CreateDbContext();
            var task = await verify.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == setup.TaskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.Done, task.Status);
            Assert.Equal(TaskPipelineStage.Closed, task.PipelineStage);
        }
    }

    [Fact]
    public async Task ReconcilesQueuedGitHubMergeOnlyWithExactRequestAndPostMergeEvidence()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: false);
        using (TenantScope.EnterUnscoped())
        {
            var candidateTree = new string('5', 40);
            var requestId = Guid.NewGuid().ToString("D");
            var postMergeHash = new string('8', 64);
            var attempt = LandingAttempt.Start(
                setup.ProjectId, setup.TaskId, setup.ReviewRunId, setup.UserId, setup.DeviceId,
                "main", setup.SourceSha, new string('2', 40), ReviewCheckCommandHash,
                "Beacon-managed async merge waiting for GitHub queue.");
            attempt.RecordPreview(
                "main", setup.RemoteBaseSha, candidateTree, new string('6', 64), 0, null);
            attempt.MarkExecutePending();
            attempt.MarkGitHubMergeQueued(requestId);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var baseRequest = Request(setup) with
            {
                LandingAttemptId = attempt.Id,
                MergeRequestId = requestId,
                CandidateTreeSha = candidateTree,
                PostMergeCheckExitCode = 0,
                PostMergeCheckOutputHash = postMergeHash
            };
            var project = await _db.Projects.SingleAsync(
                item => item.Id == setup.ProjectId, TestContext.Current.CancellationToken);
            project.Update(reviewCheckCommand: "dotnet test");
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var changedCommand = await handler.HandleAsync(
                setup.DeviceId, baseRequest, TestContext.Current.CancellationToken);
            Assert.False(changedCommand.Success);
            Assert.Equal(ErrorKind.Conflict, changedCommand.Kind);
            Assert.Contains("ReviewCheckCommand changed", changedCommand.Error ?? string.Empty, StringComparison.Ordinal);

            project.Update(reviewCheckCommand: ReviewCheckCommand);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var wrongRequest = await handler.HandleAsync(
                setup.DeviceId,
                baseRequest with { MergeRequestId = Guid.NewGuid().ToString("D") },
                TestContext.Current.CancellationToken);

            Assert.False(wrongRequest.Success);
            Assert.Equal(ErrorKind.Conflict, wrongRequest.Kind);

            var wrongAttempt = await handler.HandleAsync(
                setup.DeviceId,
                baseRequest with { LandingAttemptId = Guid.NewGuid() },
                TestContext.Current.CancellationToken);
            Assert.False(wrongAttempt.Success);
            Assert.Equal(ErrorKind.Conflict, wrongAttempt.Kind);

            var result = await handler.HandleAsync(
                setup.DeviceId, baseRequest, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            Assert.Equal("Done", result.Value!.TaskStatus);
            Assert.Equal("Closed", result.Value.PipelineStage);
            await using var verify = Factory().CreateDbContext();
            var savedAttempt = await verify.LandingAttempts.AsNoTracking()
                .SingleAsync(item => item.Id == attempt.Id, TestContext.Current.CancellationToken);
            Assert.Equal(LandingAttemptStatus.Landed, savedAttempt.Status);
            Assert.Equal(setup.MergeSha, savedAttempt.MergeCommitSha);
            Assert.Equal(0, savedAttempt.PostMergeCheckExitCode);
            Assert.Equal(postMergeHash, savedAttempt.PostMergeCheckOutputHash);
            var task = await verify.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == setup.TaskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.Done, task.Status);
            Assert.Equal(TaskPipelineStage.Closed, task.PipelineStage);
        }
    }

    [Fact]
    public async Task RejectsAutoSyncOffReconciliationFromLegacyDaemonWithoutNewCapability()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: false);
        using (TenantScope.EnterUnscoped())
        {
            var device = await _db.DaemonDevices.SingleAsync(
                item => item.Id == setup.DeviceId, TestContext.Current.CancellationToken);
            device.Heartbeat(
                """{"capabilities":["git-sync-v1","git-auto-sync-v1","git-land-v1","git-land-integration-v1"]}""",
                "{}");

            var candidateTree = new string('5', 40);
            var attempt = LandingAttempt.Start(
                setup.ProjectId, setup.TaskId, setup.ReviewRunId, setup.UserId, setup.DeviceId,
                "main", setup.SourceSha, new string('2', 40), new string('a', 64),
                "Beacon-managed landing proof from a legacy daemon.");
            attempt.RecordPreview(
                "main", setup.RemoteBaseSha, candidateTree, new string('6', 64), 0, null);
            attempt.MarkExecutePending();
            attempt.RecordLanded(setup.MergeSha, candidateTree, 0, new string('7', 64));
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var result = await handler.HandleAsync(
                setup.DeviceId, Request(setup), TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(ErrorKind.Forbidden, result.Kind);
            Assert.Contains("does not advertise", result.Error ?? string.Empty, StringComparison.Ordinal);

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
    public async Task PersistsPendingToEnqueuedTransitionForExactAttemptAndIsIdempotent()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: false);
        var requestId = Guid.NewGuid().ToString("D");
        var attempt = LandingAttempt.Start(
            setup.ProjectId, setup.TaskId, setup.ReviewRunId, setup.UserId, setup.DeviceId,
            "main", setup.SourceSha, new string('2', 40), ReviewCheckCommandHash,
            "Persist GitHub merge queue acknowledgement.");
        attempt.RecordPreview(
            "main", setup.RemoteBaseSha, new string('5', 40), new string('6', 64), 0, null);
        attempt.MarkExecutePending();
        attempt.MarkGitHubMergeQueued(requestId, "pending");

        using (TenantScope.EnterUnscoped())
        {
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var request = new DeviceRemoteLandingStatusRequest(
                setup.ProjectId,
                setup.TaskId,
                attempt.Id,
                setup.ReviewRunId,
                "acme/project-beacon",
                42,
                "https://github.com/acme/project-beacon/pull/42",
                $"beacon/task/{setup.TaskId:N}",
                "main",
                setup.SourceSha,
                requestId,
                "enqueued",
                "OPEN",
                DateTime.UtcNow);

            var wrongRequest = await handler.ReportAsyncMergeStatusAsync(
                setup.DeviceId,
                request with { MergeRequestId = Guid.NewGuid().ToString("D") },
                TestContext.Current.CancellationToken);
            Assert.False(wrongRequest.Success);
            Assert.Equal(ErrorKind.Conflict, wrongRequest.Kind);

            var closedPullRequest = await handler.ReportAsyncMergeStatusAsync(
                setup.DeviceId,
                request with { PullRequestState = "CLOSED" },
                TestContext.Current.CancellationToken);
            Assert.False(closedPullRequest.Success);
            Assert.Equal(ErrorKind.Validation, closedPullRequest.Kind);

            var pendingRegression = await handler.ReportAsyncMergeStatusAsync(
                setup.DeviceId,
                request with { MergeRequestStatus = "pending" },
                TestContext.Current.CancellationToken);
            Assert.False(pendingRegression.Success);
            Assert.Equal(ErrorKind.Validation, pendingRegression.Kind);

            var result = await handler.ReportAsyncMergeStatusAsync(
                setup.DeviceId, request, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error);
            Assert.Equal("enqueued", result.Value!.MergeRequestStatus);
            Assert.False(result.Value.AlreadyRecorded);

            var repeated = await handler.ReportAsyncMergeStatusAsync(
                setup.DeviceId, request, TestContext.Current.CancellationToken);
            Assert.True(repeated.Success, repeated.Error);
            Assert.True(repeated.Value!.AlreadyRecorded);

            var targets = await new GetDeviceAutoSyncTargetsHandler(Factory()).HandleAsync(
                setup.DeviceId, TestContext.Current.CancellationToken);
            Assert.True(targets.Success, targets.Error);
            var remoteTarget = Assert.Single(Assert.Single(targets.Value!).RemoteLandings!);
            Assert.Equal(attempt.Id, remoteTarget.LandingAttemptId);
            Assert.Equal(requestId, remoteTarget.MergeRequestId);
            Assert.Equal("enqueued", remoteTarget.MergeRequestStatus);
            Assert.True(remoteTarget.GitHubMergeQueued);

            await using var verify = Factory().CreateDbContext();
            var persisted = await verify.LandingAttempts.AsNoTracking()
                .SingleAsync(item => item.Id == attempt.Id, TestContext.Current.CancellationToken);
            Assert.Equal(LandingAttemptStatus.ExecutePending, persisted.Status);
            Assert.Equal("GitHubMergeEnqueued:" + requestId, persisted.FailureCode);
            Assert.Equal("enqueued", persisted.GitHubMergeRequestStatus);
            Assert.Null(persisted.MergeCommitSha);

            var task = await verify.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == setup.TaskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.InProgress, task.Status);
            Assert.Equal(TaskPipelineStage.Approved, task.PipelineStage);
            Assert.Empty(await verify.RemoteLandingProofs.AsNoTracking()
                .Where(proof => proof.TaskId == setup.TaskId)
                .ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await verify.AuditEvents.CountAsync(
                audit => audit.Action == "task.remote_landing.merge_enqueued",
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task RecordsOnlyTheExactVerifiedGitHubQueueFailureAndKeepsTaskOpen()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: false);
        var requestId = Guid.NewGuid().ToString("D");
        var landingAttempt = LandingAttempt.Start(
            setup.ProjectId, setup.TaskId, setup.ReviewRunId, setup.UserId, setup.DeviceId,
            "main", setup.SourceSha, new string('2', 40), ReviewCheckCommandHash,
            "Wait for GitHub's required merge queue.");
        landingAttempt.RecordPreview(
            "main", setup.RemoteBaseSha, new string('5', 40), new string('6', 64), 0, null);
        landingAttempt.MarkExecutePending();
        landingAttempt.MarkGitHubMergeQueued(requestId, "pending");
        Assert.True(landingAttempt.IsGitHubMergePending);
        Assert.False(landingAttempt.IsGitHubMergeQueued);
        using (TenantScope.EnterUnscoped())
        {
            _db.LandingAttempts.Add(landingAttempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var request = new DeviceRemoteLandingFailureRequest(
                setup.ProjectId,
                setup.TaskId,
                landingAttempt.Id,
                setup.ReviewRunId,
                "acme/project-beacon",
                42,
                "https://github.com/acme/project-beacon/pull/42",
                $"beacon/task/{setup.TaskId:N}",
                "main",
                setup.SourceSha,
                requestId,
                DateTime.UtcNow);

            var wrongRequest = await handler.ReportFailureAsync(
                setup.DeviceId,
                request with { MergeRequestId = Guid.NewGuid().ToString("D") },
                TestContext.Current.CancellationToken);
            Assert.False(wrongRequest.Success);
            Assert.Equal(ErrorKind.Conflict, wrongRequest.Kind);

            await using (var verifyBefore = Factory().CreateDbContext())
            {
                var unchanged = await verifyBefore.LandingAttempts.AsNoTracking()
                    .SingleAsync(item => item.Id == landingAttempt.Id, TestContext.Current.CancellationToken);
                Assert.Equal(LandingAttemptStatus.ExecutePending, unchanged.Status);
                Assert.Equal("GitHubMergeEnqueued:" + requestId, unchanged.FailureCode);
            }

            var result = await handler.ReportFailureAsync(
                setup.DeviceId, request, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            Assert.Equal("GitHubAsyncMergeFailed", result.Value!.FailureCode);
            Assert.Equal(LandingAttemptStatus.Failed.ToString(), result.Value.AttemptStatus);

            await using var verify = Factory().CreateDbContext();
            var failed = await verify.LandingAttempts.AsNoTracking()
                .SingleAsync(item => item.Id == landingAttempt.Id, TestContext.Current.CancellationToken);
            Assert.Equal(LandingAttemptStatus.Failed, failed.Status);
            Assert.Equal("GitHubAsyncMergeFailed", failed.FailureCode);
            Assert.Equal("GitHubAsyncMergeFailed", failed.EscalationCode);
            Assert.Null(failed.MergeCommitSha);

            var task = await verify.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == setup.TaskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.InProgress, task.Status);
            Assert.Equal(TaskPipelineStage.Approved, task.PipelineStage);
            Assert.Empty(await verify.RemoteLandingProofs.AsNoTracking()
                .Where(proof => proof.TaskId == setup.TaskId)
                .ToListAsync(TestContext.Current.CancellationToken));

            var audit = await verify.AuditEvents.AsNoTracking()
                .SingleAsync(item => item.Action == "task.remote_landing.merge_failed", TestContext.Current.CancellationToken);
            Assert.Equal(AuditActorKind.Device, audit.ActorKind);
            Assert.Equal(setup.DeviceId, audit.ActorDeviceId);
        }
    }

    [Fact]
    public async Task RecordsQueueRemovalWithoutAsyncRequestIdOnlyForExplicitQueuedAttempt()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: false);
        using (TenantScope.EnterUnscoped())
        {
            var attempt = LandingAttempt.Start(
                setup.ProjectId, setup.TaskId, setup.ReviewRunId, setup.UserId, setup.DeviceId,
                "main", setup.SourceSha, new string('2', 40), ReviewCheckCommandHash,
                "GitHub accepted the PR into its queue without returning a request UUID.");
            attempt.RecordPreview(
                "main", setup.RemoteBaseSha, new string('5', 40), new string('6', 64), 0, null);
            attempt.MarkExecutePending();
            attempt.MarkGitHubMergeQueued(null);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            // A later project setting edit does not invalidate the fact that this exact
            // GitHub queue request was rejected before any post-merge check could run.
            var project = await _db.Projects.SingleAsync(
                item => item.Id == setup.ProjectId, TestContext.Current.CancellationToken);
            project.Update(reviewCheckCommand: "dotnet test");
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var request = new DeviceRemoteLandingFailureRequest(
                setup.ProjectId,
                setup.TaskId,
                attempt.Id,
                setup.ReviewRunId,
                "acme/project-beacon",
                42,
                "https://github.com/acme/project-beacon/pull/42",
                $"beacon/task/{setup.TaskId:N}",
                "main",
                setup.SourceSha,
                MergeRequestId: null,
                VerifiedAtUtc: DateTime.UtcNow);

            var result = await handler.ReportFailureAsync(
                setup.DeviceId, request, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            Assert.Equal("GitHubAsyncMergeFailed", result.Value!.FailureCode);
            await using var verify = Factory().CreateDbContext();
            var persisted = await verify.LandingAttempts.AsNoTracking()
                .SingleAsync(item => item.Id == attempt.Id, TestContext.Current.CancellationToken);
            Assert.Equal(LandingAttemptStatus.Failed, persisted.Status);
            Assert.Equal("GitHubAsyncMergeFailed", persisted.EscalationCode);
        }
    }

    [Fact]
    public async Task RecordsClosedUnmergedPullRequestAsQueueFailureForTheExactQueuedAttempt()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: false);
        using (TenantScope.EnterUnscoped())
        {
            var attempt = LandingAttempt.Start(
                setup.ProjectId, setup.TaskId, setup.ReviewRunId, setup.UserId, setup.DeviceId,
                "main", setup.SourceSha, new string('2', 40), ReviewCheckCommandHash,
                "The queued pull request was closed without a merge.");
            attempt.RecordPreview(
                "main", setup.RemoteBaseSha, new string('5', 40), new string('6', 64), 0, null);
            attempt.MarkExecutePending();
            attempt.MarkGitHubMergeQueued(null);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var request = new DeviceRemoteLandingFailureRequest(
                setup.ProjectId,
                setup.TaskId,
                attempt.Id,
                setup.ReviewRunId,
                "acme/project-beacon",
                42,
                "https://github.com/acme/project-beacon/pull/42",
                $"beacon/task/{setup.TaskId:N}",
                "main",
                setup.SourceSha,
                MergeRequestId: null,
                VerifiedAtUtc: DateTime.UtcNow,
                FailureCode: "GitHubPullRequestClosedWithoutMerge",
                PullRequestState: "CLOSED");

            var mismatchedState = await handler.ReportFailureAsync(
                setup.DeviceId,
                request with { PullRequestState = "OPEN" },
                TestContext.Current.CancellationToken);
            Assert.False(mismatchedState.Success);
            Assert.Equal(ErrorKind.Validation, mismatchedState.Kind);

            var result = await handler.ReportFailureAsync(
                setup.DeviceId, request, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            Assert.Equal("GitHubPullRequestClosedWithoutMerge", result.Value!.FailureCode);
            Assert.Equal(LandingAttemptStatus.Failed.ToString(), result.Value.AttemptStatus);

            await using var verify = Factory().CreateDbContext();
            var failed = await verify.LandingAttempts.AsNoTracking()
                .SingleAsync(item => item.Id == attempt.Id, TestContext.Current.CancellationToken);
            Assert.Equal(LandingAttemptStatus.Failed, failed.Status);
            Assert.Equal("GitHubPullRequestClosedWithoutMerge", failed.FailureCode);
            Assert.Equal("GitHubPullRequestClosedWithoutMerge", failed.EscalationCode);
            Assert.Null(failed.MergeCommitSha);

            var task = await verify.Tasks.AsNoTracking()
                .SingleAsync(item => item.Id == setup.TaskId, TestContext.Current.CancellationToken);
            Assert.Equal(TaskItemStatus.InProgress, task.Status);
            Assert.Equal(TaskPipelineStage.Approved, task.PipelineStage);
            Assert.Empty(await verify.RemoteLandingProofs.AsNoTracking()
                .Where(proof => proof.TaskId == setup.TaskId)
                .ToListAsync(TestContext.Current.CancellationToken));

            var audit = await verify.AuditEvents.AsNoTracking()
                .SingleAsync(item => item.Action == "task.remote_landing.merge_failed", TestContext.Current.CancellationToken);
            Assert.Equal(AuditActorKind.Device, audit.ActorKind);
            Assert.Equal(setup.DeviceId, audit.ActorDeviceId);
        }
    }

    [Fact]
    public async Task RejectsQueueRemovalReportForGenericUnknownGitLandCompletion()
    {
        var setup = await CreateApprovedTaskAsync(autoSyncEnabled: false);
        using (TenantScope.EnterUnscoped())
        {
            var attempt = LandingAttempt.Start(
                setup.ProjectId, setup.TaskId, setup.ReviewRunId, setup.UserId, setup.DeviceId,
                "main", setup.SourceSha, new string('2', 40), ReviewCheckCommandHash,
                "The local GitLand completion is unknown, not a confirmed merge-queue request.");
            attempt.RecordPreview(
                "main", setup.RemoteBaseSha, new string('5', 40), new string('6', 64), 0, null);
            attempt.MarkExecutePending();
            attempt.MarkCommandCompletionUnknown();
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var handler = new ReconcileDeviceRemoteLandingHandler(Factory());
            var request = new DeviceRemoteLandingFailureRequest(
                setup.ProjectId,
                setup.TaskId,
                attempt.Id,
                setup.ReviewRunId,
                "acme/project-beacon",
                42,
                "https://github.com/acme/project-beacon/pull/42",
                $"beacon/task/{setup.TaskId:N}",
                "main",
                setup.SourceSha,
                MergeRequestId: null,
                VerifiedAtUtc: DateTime.UtcNow);

            var result = await handler.ReportFailureAsync(
                setup.DeviceId, request, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(ErrorKind.Conflict, result.Kind);
            await using var verify = Factory().CreateDbContext();
            var persisted = await verify.LandingAttempts.AsNoTracking()
                .SingleAsync(item => item.Id == attempt.Id, TestContext.Current.CancellationToken);
            Assert.Equal(LandingAttemptStatus.ExecutePending, persisted.Status);
            Assert.Equal("CommandCompletionUnknown", persisted.FailureCode);
            Assert.Null(persisted.EscalationCode);
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
            var user = User.Create($"remote-landing-{Guid.NewGuid():N}", $"remote-{Guid.NewGuid():N}@beacon.local", "hash");
            var org = Org.Create("Remote landing");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var project = Project.Create("GitHub remote", null, org.Id);
            project.SetGitPolicyJson(ProjectGitPolicyCatalog.Serialize(
                0, [], 200, 20_000, "Off", autoSyncEnabled: autoSyncEnabled));
            project.Update(reviewCheckCommand: ReviewCheckCommand);
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
                """{"capabilities":["git-sync-v1","git-auto-sync-v1","git-land-v1","git-land-integration-v1","git-remote-landing-reconcile-v1"]}""", "{}");
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
            review.SetCheckCommandHash(ReviewCheckCommandHash);
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
