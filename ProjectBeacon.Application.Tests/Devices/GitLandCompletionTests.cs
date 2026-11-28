namespace ProjectBeacon.Application.Tests.Devices;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Domain.Common;
using Application.Devices;
using Domain.Entities.Devices;
using Domain.Entities.Audit;
using Domain.Entities.Projects;
using Domain.Entities.Identity;
using Domain.Entities.Tasks;
using Domain.Entities.Roadmap;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;

public sealed class GitLandCompletionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public GitLandCompletionTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task PreviewConflict_RecordsFailedAuditWithAllowlistedBranchAndShaEvidence()
    {
        var (org, project, device, userId) = CreateScope();
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string commandHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string targetHeadSha = "dddddddddddddddddddddddddddddddddddddddd";
        var taskId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            project.Id, taskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Resolve the conflict and request a fresh review.");

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Preview",
                sourceSha, reviewTreeSha, commandHash, "main",
                null, null, null);
            var result = JsonSerializer.Serialize(new
            {
                status = "conflicted",
                targetBranch = "main",
                targetHeadSha,
                conflicts = new[] { "src/app/conflict.cs" }
            });

            await new GitLandCompletion().ApplyAsync(
                _db, command, true, result, null, TestContext.Current.CancellationToken);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.Conflicted, attempt.Status);
            var audit = await _db.AuditEvents
                .SingleAsync(item => item.TargetType == "LandingAttempt"
                    && item.TargetId == attempt.Id.ToString("D"),
                    TestContext.Current.CancellationToken);
            Assert.Equal("pipeline.landing.conflicted", audit.Action);
            Assert.Equal(AuditOutcome.Failed, audit.Outcome);
            using var details = JsonDocument.Parse(audit.DetailsJson!);
            Assert.Equal("beacon/task/" + taskId.ToString("N"),
                details.RootElement.GetProperty("sourceBranch").GetString());
            Assert.Equal("main", details.RootElement.GetProperty("targetBranch").GetString());
            Assert.Equal(sourceSha, details.RootElement.GetProperty("sourceSha").GetString());
            Assert.Equal(targetHeadSha, details.RootElement.GetProperty("targetHeadSha").GetString());
            Assert.Equal("Conflicted", details.RootElement.GetProperty("newState").GetString());
        }
    }

    [Fact]
    public async Task ExecuteCompletion_RequiresPayloadAndResultToMatchTheReviewedAttempt()
    {
        var (org, project, device, userId) = CreateScope();
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string commandHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string targetHeadSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string candidateTreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string outputHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        const string mergeCommitSha = "1111111111111111111111111111111111111111";
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            project.Id, taskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Merge reviewed task implementation.");
        attempt.RecordPreview("main", targetHeadSha, candidateTreeSha, outputHash, 0, null);
        attempt.MarkExecutePending();

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var mismatchedPayloadCommand = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Execute",
                sourceSha, reviewTreeSha, commandHash, "main",
                targetHeadSha, new string('9', 40), null);
            await new GitLandCompletion().ApplyAsync(
                _db, mismatchedPayloadCommand, true,
                LandedResult("main", sourceSha, targetHeadSha, candidateTreeSha, mergeCommitSha, outputHash),
                null, TestContext.Current.CancellationToken);
            Assert.Equal(LandingAttemptStatus.ExecutePending, attempt.Status);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Execute",
                sourceSha, reviewTreeSha, commandHash, "main",
                targetHeadSha, candidateTreeSha, null);
            await new GitLandCompletion().ApplyAsync(
                _db, command, true,
                LandedResult("main", sourceSha, new string('8', 40), candidateTreeSha, mergeCommitSha, outputHash),
                null, TestContext.Current.CancellationToken);
            Assert.Equal(LandingAttemptStatus.Failed, attempt.Status);
            Assert.Equal("InvalidLandedEvidence", attempt.FailureCode);
        }
    }

    [Fact]
    public async Task ExecuteUnknownCompletion_KeepsAttemptPendingForMarkerReconciliation()
    {
        var (org, project, device, userId) = CreateScope();
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string commandHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string targetHeadSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string candidateTreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string outputHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        const string mergeCommitSha = "1111111111111111111111111111111111111111";
        var taskId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            project.Id, taskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Keep an uncertain merge exclusive.");
        attempt.RecordPreview("main", targetHeadSha, candidateTreeSha, outputHash, 0, null);
        attempt.MarkExecutePending();

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Execute",
                sourceSha, reviewTreeSha, commandHash, "main",
                targetHeadSha, candidateTreeSha, null);
            var result = JsonSerializer.Serialize(new
            {
                status = "execute_unknown",
                targetBranch = "main",
                sourceSha,
                targetHeadSha,
                candidateTreeSha,
                mergeCommitSha,
                errorCode = "TargetReachabilityCouldNotBeVerified"
            });

            await new GitLandCompletion().ApplyAsync(
                _db, command, true, result, null, TestContext.Current.CancellationToken);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.ExecutePending, attempt.Status);
            Assert.Equal("CommandCompletionUnknown", attempt.FailureCode);
            Assert.Null(attempt.MergeCommitSha);
            Assert.Contains(
                await _db.AuditEvents.Select(item => item.Action).ToListAsync(TestContext.Current.CancellationToken),
                action => action == "pipeline.landing.execute_failed");
        }
    }

    [Fact]
    public async Task MergeQueueCompletionWithoutUuid_KeepsExactLandingAttemptPending()
    {
        var (org, project, device, userId) = CreateScope();
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string commandHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string targetHeadSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string candidateTreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string outputHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        var taskId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            project.Id, taskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Wait for the existing GitHub merge queue.");
        attempt.RecordPreview("main", targetHeadSha, candidateTreeSha, outputHash, 0, null);
        attempt.MarkExecutePending();

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Execute",
                sourceSha, reviewTreeSha, commandHash, "main",
                targetHeadSha, candidateTreeSha, null);
            var result = JsonSerializer.Serialize(new
            {
                status = "merge_queued",
                targetBranch = "main",
                sourceSha,
                targetHeadSha,
                candidateTreeSha,
                mergeRequestId = (string?)null,
                mergeRequestStatus = "enqueued"
            });

            await new GitLandCompletion().ApplyAsync(
                _db, command, true, result, null, TestContext.Current.CancellationToken);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.ExecutePending, attempt.Status);
            Assert.Equal("GitHubMergeEnqueued", attempt.FailureCode);
            Assert.Equal("enqueued", attempt.GitHubMergeRequestStatus);
            Assert.True(attempt.IsGitHubMergeQueued);
            Assert.Null(attempt.MergeCommitSha);
            Assert.True(attempt.TryReadGitHubMergeRequestId(out var mergeRequestId));
            Assert.Null(mergeRequestId);
            Assert.Contains(
                await _db.AuditEvents.Select(item => item.Action).ToListAsync(TestContext.Current.CancellationToken),
                action => action == "pipeline.landing.execute_queued");
        }
    }

    [Fact]
    public async Task MergePendingCompletion_PersistsTheRequestUuidAndDoesNotMarkTheQueueEnqueued()
    {
        var (org, project, device, userId) = CreateScope();
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string commandHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string targetHeadSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string candidateTreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string outputHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        var requestId = Guid.NewGuid().ToString("D");
        var taskId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            project.Id, taskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Wait for the existing GitHub merge queue.");
        attempt.RecordPreview("main", targetHeadSha, candidateTreeSha, outputHash, 0, null);
        attempt.MarkExecutePending();

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Execute",
                sourceSha, reviewTreeSha, commandHash, "main",
                targetHeadSha, candidateTreeSha, null);
            var result = JsonSerializer.Serialize(new
            {
                status = "merge_queued",
                targetBranch = "main",
                sourceSha,
                targetHeadSha,
                candidateTreeSha,
                mergeRequestId = requestId,
                mergeRequestStatus = "pending"
            });

            await new GitLandCompletion().ApplyAsync(
                _db, command, true, result, null, TestContext.Current.CancellationToken);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.ExecutePending, attempt.Status);
            Assert.Equal("GitHubMergePending:" + requestId, attempt.FailureCode);
            Assert.Equal("pending", attempt.GitHubMergeRequestStatus);
            Assert.True(attempt.IsGitHubMergePending);
            Assert.False(attempt.IsGitHubMergeQueued);
            Assert.True(attempt.IsGitHubMergeAccepted);
            Assert.Null(attempt.MergeCommitSha);
            Assert.True(attempt.TryReadGitHubMergeRequestId(out var storedRequestId));
            Assert.Equal(requestId, storedRequestId);
            Assert.Contains(
                await _db.AuditEvents.Select(item => item.Action).ToListAsync(TestContext.Current.CancellationToken),
                action => action == "pipeline.landing.execute_queued");
        }
    }

    [Fact]
    public async Task NonObjectResult_KeepsMutatingActionsRecoverableAndFailsPreview()
    {
        var (org, project, device, userId) = CreateScope();
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string commandHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string targetHeadSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string candidateTreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string outputHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

        var executeTaskId = Guid.NewGuid();
        var executeAttempt = LandingAttempt.Start(
            project.Id, executeTaskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Recover an ambiguous execution result.");
        executeAttempt.RecordPreview("main", targetHeadSha, candidateTreeSha, outputHash, 0, null);
        executeAttempt.MarkExecutePending();

        var previewTaskId = Guid.NewGuid();
        var previewAttempt = LandingAttempt.Start(
            project.Id, previewTaskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Fail an invalid preview result.");

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.AddRange(executeAttempt, previewAttempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var executeCommand = CreateCommand(
                device.Id, project.Id, userId, executeAttempt.Id, executeTaskId, "Execute",
                sourceSha, reviewTreeSha, commandHash, "main", targetHeadSha, candidateTreeSha, null);
            await new GitLandCompletion().ApplyAsync(
                _db, executeCommand, true, "[]", null, TestContext.Current.CancellationToken);

            var previewCommand = CreateCommand(
                device.Id, project.Id, userId, previewAttempt.Id, previewTaskId, "Preview",
                sourceSha, reviewTreeSha, commandHash, "main", null, null, null);
            await new GitLandCompletion().ApplyAsync(
                _db, previewCommand, true, "[]", null, TestContext.Current.CancellationToken);

            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.ExecutePending, executeAttempt.Status);
            Assert.Equal("CommandCompletionUnknown", executeAttempt.FailureCode);
            Assert.Equal(LandingAttemptStatus.Failed, previewAttempt.Status);
            Assert.Equal("InvalidGitLandResult", previewAttempt.FailureCode);
        }
    }

    [Fact]
    public async Task ExecuteCompletion_RecordsOnlyThePreviewedMergeEvidence()
    {
        var (org, project, device, userId) = CreateScope();
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string commandHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string targetHeadSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string candidateTreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string outputHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        const string mergeCommitSha = "1111111111111111111111111111111111111111";
        var taskId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            project.Id, taskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Merge reviewed task implementation.");
        attempt.RecordPreview("main", targetHeadSha, candidateTreeSha, outputHash, 0, null);
        attempt.MarkExecutePending();

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Execute",
                sourceSha, reviewTreeSha, commandHash, "main",
                targetHeadSha, candidateTreeSha, null);
            await new GitLandCompletion().ApplyAsync(
                _db, command, true,
                LandedResult("main", sourceSha, targetHeadSha, candidateTreeSha, mergeCommitSha, outputHash),
                null, TestContext.Current.CancellationToken);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.Landed, attempt.Status);
            Assert.Equal(mergeCommitSha, attempt.MergeCommitSha);
            Assert.Equal(candidateTreeSha, attempt.CandidateTreeSha);
            Assert.Equal(outputHash, attempt.PostMergeCheckOutputHash);
            Assert.Contains(await _db.AuditEvents.Select(e => e.Action).ToListAsync(
                TestContext.Current.CancellationToken), action => action == "pipeline.landing.completed");
        }
    }

    [Fact]
    public async Task VerifiedHotfixLanding_CreatesOneOrdinaryForwardPortTask()
    {
        var (org, project, device, userId) = CreateScope();
        const string deployedSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string sourceSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string reviewTreeSha = "cccccccccccccccccccccccccccccccccccccccc";
        const string commandHash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        const string targetHeadSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string candidateTreeSha = "ffffffffffffffffffffffffffffffffffffffff";
        const string outputHash = "1111111111111111111111111111111111111111111111111111111111111111";
        const string mergeCommitSha = "2222222222222222222222222222222222222222";
        var release = Release.Create("Production 1.2.3", null, project.Id, "1.2.3", "main", 1);
        var hotfix = TaskItem.Create(
            "Fix production regression", project.Id, TaskPriority.High, TaskType.Bug,
            "Production fix for an observed regression.");
        hotfix.SetHotfixOrigin(release.Id, deployedSha, "main", Guid.NewGuid());
        hotfix.PinPipelineDevice(device.Id);
        var attempt = LandingAttempt.Start(
            project.Id, hotfix.Id, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Land verified production Hotfix.");
        attempt.RecordPreview("main", targetHeadSha, candidateTreeSha, outputHash, 0, null);
        attempt.MarkExecutePending();

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.Releases.Add(release);
            _db.Tasks.Add(hotfix);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, hotfix.Id, "Execute",
                sourceSha, reviewTreeSha, commandHash, "main",
                targetHeadSha, candidateTreeSha, null);
            var result = LandedResult(
                "main", sourceSha, targetHeadSha, candidateTreeSha, mergeCommitSha, outputHash);

            await new GitLandCompletion().ApplyAsync(
                _db, command, true, result, null, TestContext.Current.CancellationToken);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.Landed, attempt.Status);
            var forwardPort = await _db.Tasks.SingleAsync(
                task => task.ProjectId == project.Id && task.ForwardPortOfTaskId == hotfix.Id,
                TestContext.Current.CancellationToken);
            Assert.Equal(TaskType.Task, forwardPort.Type);
            Assert.Equal(TaskPriority.High, forwardPort.Priority);
            Assert.Equal(device.Id, forwardPort.PipelineDeviceId);
            Assert.Contains("Production deployed SHA: " + deployedSha, forwardPort.Description);
            Assert.Contains("Hotfix landing merge commit: " + mergeCommitSha, forwardPort.Description);

            await new GitLandCompletion().ApplyAsync(
                _db, command, true, result, null, TestContext.Current.CancellationToken);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, await _db.Tasks.CountAsync(
                task => task.ProjectId == project.Id && task.ForwardPortOfTaskId == hotfix.Id,
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task UnverifiedHotfixLanding_DoesNotCreateForwardPortTask()
    {
        var (org, project, device, userId) = CreateScope();
        const string deployedSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string sourceSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string reviewTreeSha = "cccccccccccccccccccccccccccccccccccccccc";
        const string commandHash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        const string targetHeadSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string candidateTreeSha = "ffffffffffffffffffffffffffffffffffffffff";
        var release = Release.Create("Production 1.2.3", null, project.Id, "1.2.3", "main", 1);
        var hotfix = TaskItem.Create("Unverified Hotfix", project.Id, TaskPriority.High, TaskType.Bug);
        hotfix.SetHotfixOrigin(release.Id, deployedSha, "main", Guid.NewGuid());
        hotfix.PinPipelineDevice(device.Id);
        var attempt = LandingAttempt.Start(
            project.Id, hotfix.Id, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Do not forward-port unverified work.");
        attempt.RecordPreview("main", targetHeadSha, candidateTreeSha,
            new string('1', 64), 0, null);
        attempt.MarkExecutePending();

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.Releases.Add(release);
            _db.Tasks.Add(hotfix);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, hotfix.Id, "Execute",
                sourceSha, reviewTreeSha, commandHash, "main",
                targetHeadSha, candidateTreeSha, null);
            var unverified = JsonSerializer.Serialize(new
            {
                status = "landed_unverified",
                targetBranch = "main",
                sourceSha,
                targetHeadSha,
                candidateTreeSha,
                mergeCommitSha = "2222222222222222222222222222222222222222",
                errorCode = "PostMergeVerificationFailed",
                postMergeCheckExitCode = 1
            });

            await new GitLandCompletion().ApplyAsync(
                _db, command, true, unverified, null, TestContext.Current.CancellationToken);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.LandedUnverified, attempt.Status);
            Assert.Empty(await _db.Tasks.Where(
                task => task.ProjectId == project.Id && task.ForwardPortOfTaskId == hotfix.Id)
                .ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ExecutePreflightFailure_IsTerminalAndDoesNotBecomeCompletionUnknown()
    {
        var (org, project, device, userId) = CreateScope();
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string commandHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string targetHeadSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string candidateTreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string outputHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        var taskId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            project.Id, taskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Record a deterministic preflight rejection.");
        attempt.RecordPreview("main", targetHeadSha, candidateTreeSha, outputHash, 0, null);
        attempt.MarkExecutePending();

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Execute",
                sourceSha, reviewTreeSha, commandHash, "main",
                targetHeadSha, candidateTreeSha, null);
            await new GitLandCompletion().ApplyAsync(
                _db, command, true,
                JsonSerializer.Serialize(new { status = "failed", errorCode = "TaskSourceChangedSinceReview" }),
                null, TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.Failed, attempt.Status);
            Assert.Equal("TaskSourceChangedSinceReview", attempt.FailureCode);
        }
    }

    [Fact]
    public async Task RevertPreflightFailure_IsRetryableWithoutAssumingTheTargetWasChanged()
    {
        var (org, project, device, userId) = CreateScope();
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string commandHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string targetHeadSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string candidateTreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string outputHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        const string mergeCommitSha = "1111111111111111111111111111111111111111";
        var taskId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            project.Id, taskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Keep a failed revert explicitly retryable.");
        attempt.RecordPreview("main", targetHeadSha, candidateTreeSha, outputHash, 0, null);
        attempt.MarkExecutePending();
        attempt.RecordLanded(mergeCommitSha, candidateTreeSha, 0, outputHash);
        attempt.MarkRevertPending();

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Revert",
                sourceSha, reviewTreeSha, commandHash, "main",
                null, null, mergeCommitSha);
            await new GitLandCompletion().ApplyAsync(
                _db, command, true,
                JsonSerializer.Serialize(new { status = "failed", errorCode = "TargetAdvancedBeyondLanding" }),
                null, TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.RevertFailed, attempt.Status);
            Assert.Equal("TargetAdvancedBeyondLanding", attempt.FailureCode);
        }
    }

    [Fact]
    public async Task RevertCompletion_RequiresTheRecordedLandingCommit()
    {
        var (org, project, device, userId) = CreateScope();
        const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string reviewTreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string commandHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string targetHeadSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string candidateTreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string outputHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        const string mergeCommitSha = "1111111111111111111111111111111111111111";
        const string revertCommitSha = "2222222222222222222222222222222222222222";
        var taskId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            project.Id, taskId, Guid.NewGuid(), userId, device.Id,
            "main", sourceSha, reviewTreeSha, commandHash, "Merge reviewed task implementation.");
        attempt.RecordPreview("main", targetHeadSha, candidateTreeSha, outputHash, 0, null);
        attempt.MarkExecutePending();
        attempt.RecordLanded(mergeCommitSha, candidateTreeSha, 0, outputHash);
        attempt.MarkRevertPending();

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var wrongCommand = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Revert",
                sourceSha, reviewTreeSha, commandHash, "main",
                null, null, new string('9', 40));
            await new GitLandCompletion().ApplyAsync(
                _db, wrongCommand, true,
                RevertedResult("main", mergeCommitSha, revertCommitSha),
                null, TestContext.Current.CancellationToken);
            Assert.Equal(LandingAttemptStatus.RevertPending, attempt.Status);

            var command = CreateCommand(
                device.Id, project.Id, userId, attempt.Id, taskId, "Revert",
                sourceSha, reviewTreeSha, commandHash, "main",
                null, null, mergeCommitSha);
            await new GitLandCompletion().ApplyAsync(
                _db, command, true,
                RevertedResult("main", mergeCommitSha, revertCommitSha),
                null, TestContext.Current.CancellationToken);

            Assert.Equal(LandingAttemptStatus.Reverted, attempt.Status);
            Assert.Equal(revertCommitSha, attempt.RevertCommitSha);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedRevertAttempt_BlocksAnotherLandingForTheSameTask(bool worktreeRefreshFailed)
    {
        var (org, project, device, userId) = CreateScope();
        var taskId = Guid.NewGuid();
        var attempt = LandingAttempt.Start(
            project.Id, taskId, Guid.NewGuid(), userId, device.Id,
            "main", new string('a', 40), new string('b', 40), new string('c', 64),
            "Keep unresolved revert state exclusive.");
        attempt.RecordPreview("main", new string('d', 40), new string('e', 40), new string('f', 64), 0, null);
        attempt.MarkExecutePending();
        attempt.RecordLanded(new string('1', 40), new string('e', 40), 0, new string('2', 64));
        attempt.MarkRevertPending();
        if (worktreeRefreshFailed)
            attempt.RecordRevertedUnverified(new string('3', 40), "TargetWorktreeUpdateFailed");
        else
            attempt.Fail("TargetAdvancedAfterLanding");

        Assert.Equal(
            worktreeRefreshFailed ? LandingAttemptStatus.RevertedUnverified : LandingAttemptStatus.RevertFailed,
            attempt.Status);

        using (TenantScope.EnterUnscoped())
        {
            _db.Orgs.Add(org);
            _db.Projects.Add(project);
            _db.DaemonDevices.Add(device);
            _db.LandingAttempts.Add(attempt);
            await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var secondAttempt = LandingAttempt.Start(
                project.Id, taskId, Guid.NewGuid(), userId, device.Id,
                "main", new string('4', 40), new string('5', 40), new string('6', 64),
                "Must not overlap an unresolved revert.");
            _db.LandingAttempts.Add(secondAttempt);

            await Assert.ThrowsAsync<DbUpdateException>(
                () => _db.SaveChangesAsync(TestContext.Current.CancellationToken));
        }
    }

    private (Org Org, Project Project, DaemonDevice Device, Guid UserId) CreateScope()
    {
        var org = Org.Create("GitLand completion");
        var project = Project.Create("GitLand", null, org.Id);
        var user = User.Create(
            "gitland-" + Guid.NewGuid().ToString("N"),
            "gitland-" + Guid.NewGuid().ToString("N") + "@beacon.local",
            "hash");
        _db.Users.Add(user);
        var device = DaemonDevice.Create(
            "git-land-test", user.Id, "git-land-fp-" + Guid.NewGuid().ToString("N"),
            "token-hash", "bcd_gitland_" + Guid.NewGuid().ToString("N"));
        return (org, project, device, user.Id);
    }

    private static WorkstationCommand CreateCommand(
        Guid deviceId, Guid projectId, Guid userId, Guid attemptId, Guid taskId,
        string action, string sourceSha, string reviewTreeSha, string checkCommandHash,
        string targetBranch, string? targetHeadSha, string? targetTreeSha, string? mergeCommitSha)
    {
        var payload = new
        {
            projectId,
            taskId,
            landingAttemptId = attemptId,
            action,
            targetBranch,
            sourceSha,
            reviewTreeSha,
            checkCommandHash,
            targetHeadSha,
            targetTreeSha,
            mergeCommitSha
        };
        return WorkstationCommand.Create(
            deviceId, WorkstationCommandKind.GitLand, JsonSerializer.Serialize(payload), projectId, userId);
    }

    private static string LandedResult(
        string targetBranch, string sourceSha, string targetHeadSha, string candidateTreeSha,
        string mergeCommitSha, string outputHash) =>
        JsonSerializer.Serialize(new
        {
            status = "landed",
            targetBranch,
            sourceSha,
            targetHeadSha,
            candidateTreeSha,
            mergeCommitSha,
            postMergeCheckExitCode = 0,
            postMergeCheckOutputHash = outputHash
        });

    private static string RevertedResult(string targetBranch, string mergeCommitSha, string revertCommitSha) =>
        JsonSerializer.Serialize(new { status = "reverted", targetBranch, mergeCommitSha, revertCommitSha });
}
