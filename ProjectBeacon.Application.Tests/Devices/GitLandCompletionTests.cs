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
