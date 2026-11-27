namespace ProjectBeacon.Application.Devices;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Releases;
using Application.Persistence;
using Application.Projects;
using Domain.Common;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Exact GitHub merge evidence reported by the task's pinned workstation.</summary>
public sealed record DeviceRemoteLandingReconciliationRequest(
    Guid ProjectId,
    Guid TaskId,
    Guid ReviewRunId,
    string Repository,
    int PullRequestNumber,
    string PullRequestUrl,
    string SourceBranch,
    string TargetBranch,
    string SourceSha,
    string MergeCommitSha,
    string RemoteBaseSha,
    DateTime MergedAtUtc,
    DateTime VerifiedAtUtc,
    Guid? LandingAttemptId = null,
    string? MergeRequestId = null,
    string? CandidateTreeSha = null,
    int? PostMergeCheckExitCode = null,
    string? PostMergeCheckOutputHash = null);

/// <summary>Exact GitHub asynchronous merge failure reported by the pinned workstation.</summary>
public sealed record DeviceRemoteLandingFailureRequest(
    Guid ProjectId,
    Guid TaskId,
    Guid LandingAttemptId,
    Guid ReviewRunId,
    string Repository,
    int PullRequestNumber,
    string PullRequestUrl,
    string SourceBranch,
    string TargetBranch,
    string SourceSha,
    string? MergeRequestId,
    DateTime VerifiedAtUtc,
    string FailureCode = "GitHubAsyncMergeFailed",
    string PullRequestState = "OPEN");

public sealed record DeviceRemoteLandingFailureResult(
    Guid LandingAttemptId,
    Guid ProjectId,
    Guid TaskId,
    string FailureCode,
    string AttemptStatus);

public sealed record DeviceRemoteLandingStatusRequest(
    Guid ProjectId,
    Guid TaskId,
    Guid LandingAttemptId,
    Guid ReviewRunId,
    string Repository,
    int PullRequestNumber,
    string PullRequestUrl,
    string SourceBranch,
    string TargetBranch,
    string SourceSha,
    string MergeRequestId,
    string MergeRequestStatus,
    string PullRequestState,
    DateTime VerifiedAtUtc);

public sealed record DeviceRemoteLandingStatusResult(
    Guid LandingAttemptId,
    Guid ProjectId,
    Guid TaskId,
    string MergeRequestStatus,
    bool AlreadyRecorded);

public sealed record DeviceRemoteLandingReconciliationResult(
    Guid ProofId,
    Guid ProjectId,
    Guid TaskId,
    int PullRequestNumber,
    string PullRequestUrl,
    string SourceSha,
    string MergeCommitSha,
    string TargetBranch,
    string RemoteBaseSha,
    string TaskStatus,
    string PipelineStage,
    bool AlreadyRecorded);

/// <summary>
/// Records a workstation-verified GitHub PR merge only when it matches the task's immutable
/// workstation pin and latest successful Review proof. It never invokes a Git merge or GH merge.
/// </summary>
public sealed class ReconcileDeviceRemoteLandingHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public ReconcileDeviceRemoteLandingHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    /// <summary>
    /// Records a definitive GitHub async merge failure for the exact queued request.
    /// This is a device-originated status report, never an instruction to merge or mutate refs.
    /// </summary>
    public async Task<Result<DeviceRemoteLandingFailureResult>> ReportFailureAsync(
        Guid deviceId,
        DeviceRemoteLandingFailureRequest request,
        CancellationToken ct = default)
    {
        Guid? parsedMergeRequestId = null;
        if (request.MergeRequestId is not null)
        {
            if (!Guid.TryParse(request.MergeRequestId, out var parsed) || parsed == Guid.Empty)
                return Result.Failure<DeviceRemoteLandingFailureResult>(
                    "The GitHub async merge request ID is invalid.", ErrorKind.Validation);
            parsedMergeRequestId = parsed;
        }

        var expectedPullRequestState = request.FailureCode switch
        {
            "GitHubAsyncMergeFailed" => "OPEN",
            "GitHubPullRequestClosedWithoutMerge" => "CLOSED",
            _ => null
        };
        if (deviceId == Guid.Empty || request.ProjectId == Guid.Empty
            || request.TaskId == Guid.Empty || request.LandingAttemptId == Guid.Empty
            || request.ReviewRunId == Guid.Empty || request.PullRequestNumber < 1
            || request.VerifiedAtUtc == default
            || request.VerifiedAtUtc > DateTime.UtcNow.AddMinutes(2)
            || !IsValidGitHubRepository(request.Repository)
            || !IsCanonicalPullRequestUrl(request.PullRequestUrl, request.Repository, request.PullRequestNumber)
            || !string.Equals(request.SourceBranch, $"beacon/task/{request.TaskId:N}", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.TargetBranch)
            || !IsGitSha(request.SourceSha)
            || expectedPullRequestState is null
            || !string.Equals(request.PullRequestState, expectedPullRequestState, StringComparison.Ordinal))
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "The GitHub async merge failure identity or unmerged PR state is invalid.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == deviceId, ct);
        if (device is null || device.IsRevoked
            || !WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.AutoSyncV1))
            return Result.Forbidden<DeviceRemoteLandingFailureResult>(
                "An active AutoSync-capable device token is required.");

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == request.ProjectId, ct);
        if (project is null)
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "Project not found.", ErrorKind.NotFound);
        if (!ProjectGitPolicyCatalog.TryRead(
                project.GitPolicyJson, project.GitPolicyRevision, out var gitPolicy))
            return Result.Forbidden<DeviceRemoteLandingFailureResult>(
                "The project Git policy is invalid; remote landing failure reports are disabled.");
        if (!gitPolicy.AutoSyncEnabled
            && !WorkstationCapabilities.Supports(
                device.ProbeJson, WorkstationCapabilities.RemoteLandingReconcileV1))
            return Result.Forbidden<DeviceRemoteLandingFailureResult>(
                "This workstation does not advertise the AutoSync-independent remote landing protocol.");

        var runtimeAttached = await db.ProjectRuntimes.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(runtime => runtime.ProjectId == request.ProjectId
                && runtime.DeviceId == deviceId, ct);
        if (!runtimeAttached)
            return Result.Forbidden<DeviceRemoteLandingFailureResult>(
                "The device is not attached to the requested project.");

        var task = await db.Tasks.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == request.TaskId
                && item.ProjectId == request.ProjectId, ct);
        if (task is null)
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "Task not found.", ErrorKind.NotFound);
        if (task.PipelineDeviceId != deviceId)
            return Result.Forbidden<DeviceRemoteLandingFailureResult>(
                "Only the workstation pinned to this task can report its GitHub merge failure.");
        if (task.Status != TaskItemStatus.InProgress
            || task.PipelineStage != TaskPipelineStage.Approved)
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "The task must remain InProgress and pipeline-approved while a queued merge is monitored.",
                ErrorKind.Conflict);

        var attempt = await db.LandingAttempts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == request.LandingAttemptId
                && item.ProjectId == request.ProjectId
                && item.TaskId == request.TaskId, ct);
        if (attempt is null)
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "Landing attempt not found.", ErrorKind.NotFound);

        var latestAttemptId = await db.LandingAttempts.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.ProjectId == request.ProjectId && item.TaskId == request.TaskId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(ct);
        if (latestAttemptId != attempt.Id)
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "The GitHub async merge failure belongs to an older landing attempt. The latest attempt must be resolved instead.",
                ErrorKind.Conflict);

        var expectedFailureCode = request.FailureCode;
        if (attempt.Status != LandingAttemptStatus.ExecutePending
            || attempt.DeviceId != deviceId
            || attempt.ReviewRunId != request.ReviewRunId
            || !attempt.IsGitHubMergeAccepted
            || !attempt.TryReadGitHubMergeRequestId(out var expectedRequestId)
            || !string.Equals(expectedRequestId, parsedMergeRequestId?.ToString("D"), StringComparison.Ordinal)
            || !string.Equals(attempt.SourceSha, request.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(attempt.SourceBranch, request.SourceBranch, StringComparison.Ordinal)
            || !string.Equals(attempt.TargetBranch, request.TargetBranch, StringComparison.Ordinal))
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "The GitHub async merge failure does not match the exact accepted Beacon async landing attempt.",
                ErrorKind.Conflict);

        var reviewRun = await db.ReviewRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(run => run.ProjectId == request.ProjectId && run.TaskId == request.TaskId)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .FirstOrDefaultAsync(ct);
        if (reviewRun is null || reviewRun.Id != attempt.ReviewRunId
            || !reviewRun.IsLandingProof()
            || reviewRun.CheckedDeviceId != deviceId
            || !string.Equals(reviewRun.CheckedSourceSha, attempt.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reviewRun.CheckedBaseBranch, attempt.TargetBranch, StringComparison.Ordinal)
            || !string.Equals(reviewRun.CheckCommandHash, attempt.ReviewCommandHash, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "The queued merge failure does not match the latest successful Review proof.",
                ErrorKind.Conflict);

        // This is a definitive failure report, not a merge or post-merge proof.
        // The attempt is still bound to its immutable successful ReviewRun/hash above;
        // later edits to the project's mutable ReviewCheckCommand must not prevent a
        // failed queue request from leaving ExecutePending.

        var latestVerdict = await db.ReviewVerdicts.IgnoreQueryFilters().AsNoTracking()
            .Where(verdict => verdict.ProjectId == request.ProjectId && verdict.TaskId == request.TaskId)
            .OrderByDescending(verdict => verdict.CreatedAt)
            .ThenByDescending(verdict => verdict.Id)
            .FirstOrDefaultAsync(ct);
        if (latestVerdict is null || latestVerdict.Kind != ReviewVerdictKind.Approve)
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "The latest pipeline review verdict must still be Approve.", ErrorKind.Conflict);

        var activeSession = await db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(session => session.ProjectId == request.ProjectId
                && session.TaskId == request.TaskId
                && (session.Status == SessionStatus.Ready
                    || session.Status == SessionStatus.Active
                    || session.Status == SessionStatus.WaitingForUser), ct);
        if (activeSession)
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "A pipeline session is still active or queued for this task. The merge failure was not recorded.",
                ErrorKind.Conflict);

        try
        {
            attempt.Fail(expectedFailureCode);
            attempt.SetEscalationCode(expectedFailureCode);
        }
        catch (InvalidOperationException exception)
        {
            return Result.Failure<DeviceRemoteLandingFailureResult>(exception.Message, ErrorKind.Conflict);
        }

        var actor = new ActorContext(
            ActorType.Device, null, false, null, deviceId,
            request.ProjectId, project.OrgId, ApiTokenCapability.None);
        db.AuditEvents.Add(AuditEventFactory.ForActor(
            actor,
            "task.remote_landing.merge_failed",
            "LandingAttempt",
            attempt.Id.ToString("D"),
            project.OrgId,
            request.ProjectId,
            detailsJson: JsonSerializer.Serialize(new
            {
                deviceId,
                sourceBranch = request.SourceBranch,
                targetBranch = request.TargetBranch,
                sourceSha = request.SourceSha,
                reviewRunId = request.ReviewRunId.ToString("D"),
                policyCode = expectedFailureCode,
                resultCode = request.FailureCode == "GitHubPullRequestClosedWithoutMerge"
                    ? "QueuedPullRequestClosed"
                    : "QueuedMergeFailed"
            })));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "The landing attempt changed while the GitHub merge failure was being recorded. Refresh and retry.",
                ErrorKind.Conflict);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<DeviceRemoteLandingFailureResult>(
                "The queued GitHub merge failure could not be recorded because of a concurrent update.",
                ErrorKind.Conflict);
        }

        return Result.Ok(new DeviceRemoteLandingFailureResult(
            attempt.Id, attempt.ProjectId, attempt.TaskId, expectedFailureCode,
            attempt.Status.ToString()));
    }


    /// <summary>
    /// Persists the pinned workstation's observation that the exact pending GitHub
    /// async merge request entered the merge queue. This transition is monotonic:
    /// it neither closes the task nor records a successful landing proof.
    /// </summary>
    public async Task<Result<DeviceRemoteLandingStatusResult>> ReportAsyncMergeStatusAsync(
        Guid deviceId,
        DeviceRemoteLandingStatusRequest request,
        CancellationToken ct = default)
    {
        if (request.MergeRequestStatus != "enqueued"
            || request.PullRequestState != "OPEN"
            || deviceId == Guid.Empty
            || request.ProjectId == Guid.Empty
            || request.TaskId == Guid.Empty
            || request.LandingAttemptId == Guid.Empty
            || request.ReviewRunId == Guid.Empty
            || request.PullRequestNumber < 1
            || request.VerifiedAtUtc == default
            || request.VerifiedAtUtc > DateTime.UtcNow.AddMinutes(2)
            || !Guid.TryParse(request.MergeRequestId, out var requestId)
            || requestId == Guid.Empty
            || !IsValidGitHubRepository(request.Repository)
            || !IsCanonicalPullRequestUrl(request.PullRequestUrl, request.Repository, request.PullRequestNumber)
            || !string.Equals(request.SourceBranch, $"beacon/task/{request.TaskId:N}", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.TargetBranch)
            || !IsGitSha(request.SourceSha))
            return Result.Failure<DeviceRemoteLandingStatusResult>(
                "The GitHub enqueued status identity, request UUID, or open PR state is invalid.", ErrorKind.Validation);

        var normalizedRequestId = requestId.ToString("D");
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == deviceId, ct);
        if (device is null || device.IsRevoked
            || !WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.AutoSyncV1))
            return Result.Forbidden<DeviceRemoteLandingStatusResult>(
                "An active AutoSync-capable device token is required.");

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == request.ProjectId, ct);
        if (project is null)
            return Result.Failure<DeviceRemoteLandingStatusResult>(
                "Project not found.", ErrorKind.NotFound);
        if (!ProjectGitPolicyCatalog.TryRead(
                project.GitPolicyJson, project.GitPolicyRevision, out var gitPolicy))
            return Result.Forbidden<DeviceRemoteLandingStatusResult>(
                "The project Git policy is invalid; remote landing status reports are disabled.");
        if (!gitPolicy.AutoSyncEnabled
            && !WorkstationCapabilities.Supports(
                device.ProbeJson, WorkstationCapabilities.RemoteLandingReconcileV1))
            return Result.Forbidden<DeviceRemoteLandingStatusResult>(
                "This workstation does not advertise the AutoSync-independent remote landing protocol.");

        var runtimeAttached = await db.ProjectRuntimes.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(runtime => runtime.ProjectId == request.ProjectId
                && runtime.DeviceId == deviceId, ct);
        if (!runtimeAttached)
            return Result.Forbidden<DeviceRemoteLandingStatusResult>(
                "The device is not attached to the requested project.");

        var task = await db.Tasks.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == request.TaskId
                && item.ProjectId == request.ProjectId, ct);
        if (task is null)
            return Result.Failure<DeviceRemoteLandingStatusResult>(
                "Task not found.", ErrorKind.NotFound);
        if (task.PipelineDeviceId != deviceId)
            return Result.Forbidden<DeviceRemoteLandingStatusResult>(
                "Only the workstation pinned to this task can report its GitHub merge-queue status.");
        if (task.Status != TaskItemStatus.InProgress
            || task.PipelineStage != TaskPipelineStage.Approved)
            return Result.Failure<DeviceRemoteLandingStatusResult>(
                "The task must remain InProgress and pipeline-approved while a queued merge is monitored.",
                ErrorKind.Conflict);

        var attempt = await db.LandingAttempts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == request.LandingAttemptId
                && item.ProjectId == request.ProjectId
                && item.TaskId == request.TaskId, ct);
        if (attempt is null)
            return Result.Failure<DeviceRemoteLandingStatusResult>(
                "Landing attempt not found.", ErrorKind.NotFound);

        var latestAttemptId = await db.LandingAttempts.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.ProjectId == request.ProjectId && item.TaskId == request.TaskId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(ct);
        if (latestAttemptId != attempt.Id)
            return Result.Failure<DeviceRemoteLandingStatusResult>(
                "The enqueue observation belongs to an older landing attempt.", ErrorKind.Conflict);

        if (attempt.Status != LandingAttemptStatus.ExecutePending
            || attempt.DeviceId != deviceId
            || attempt.ReviewRunId != request.ReviewRunId
            || !string.Equals(attempt.SourceSha, request.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(attempt.SourceBranch, request.SourceBranch, StringComparison.Ordinal)
            || !string.Equals(attempt.TargetBranch, request.TargetBranch, StringComparison.Ordinal)
            || !attempt.TryReadGitHubMergeRequestId(out var expectedRequestId)
            || !string.Equals(expectedRequestId, normalizedRequestId, StringComparison.Ordinal))
            return Result.Failure<DeviceRemoteLandingStatusResult>(
                "The GitHub enqueue observation does not match the exact pending Beacon landing attempt.",
                ErrorKind.Conflict);

        var reviewRun = await db.ReviewRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(run => run.ProjectId == request.ProjectId && run.TaskId == request.TaskId)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .FirstOrDefaultAsync(ct);
        if (reviewRun is null || reviewRun.Id != attempt.ReviewRunId
            || !reviewRun.IsLandingProof()
            || reviewRun.CheckedDeviceId != deviceId
            || !string.Equals(reviewRun.CheckedSourceSha, attempt.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reviewRun.CheckedBaseBranch, attempt.TargetBranch, StringComparison.Ordinal)
            || !string.Equals(reviewRun.CheckCommandHash, attempt.ReviewCommandHash, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<DeviceRemoteLandingStatusResult>(
                "The enqueue observation does not match the latest successful Review proof.", ErrorKind.Conflict);

        var verdict = await db.ReviewVerdicts.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.ProjectId == request.ProjectId && item.TaskId == request.TaskId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(ct);
        if (verdict is null || verdict.Kind != ReviewVerdictKind.Approve)
            return Result.Failure<DeviceRemoteLandingStatusResult>(
                "The latest pipeline review verdict must still be Approve.", ErrorKind.Conflict);

        var activeSession = await db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(session => session.ProjectId == request.ProjectId
                && session.TaskId == request.TaskId
                && (session.Status == SessionStatus.Ready
                    || session.Status == SessionStatus.Active
                    || session.Status == SessionStatus.WaitingForUser), ct);
        if (activeSession)
            return Result.Failure<DeviceRemoteLandingStatusResult>(
                "A pipeline session is still active or queued for this task. The enqueue status was not recorded.",
                ErrorKind.Conflict);

        bool alreadyRecorded;
        try
        {
            alreadyRecorded = !attempt.RecordGitHubMergeEnqueued(normalizedRequestId);
        }
        catch (InvalidOperationException exception)
        {
            return Result.Failure<DeviceRemoteLandingStatusResult>(exception.Message, ErrorKind.Conflict);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<DeviceRemoteLandingStatusResult>(exception.Message, ErrorKind.Validation);
        }

        if (!alreadyRecorded)
        {
            var actor = new ActorContext(
                ActorType.Device, null, false, null, deviceId,
                request.ProjectId, project.OrgId, ApiTokenCapability.None);
            db.AuditEvents.Add(AuditEventFactory.ForActor(
                actor,
                "task.remote_landing.merge_enqueued",
                "LandingAttempt",
                attempt.Id.ToString("D"),
                project.OrgId,
                request.ProjectId,
                detailsJson: JsonSerializer.Serialize(new
                {
                    deviceId,
                    repository = request.Repository,
                    pullRequestNumber = request.PullRequestNumber,
                    pullRequestUrl = request.PullRequestUrl,
                    sourceBranch = request.SourceBranch,
                    targetBranch = request.TargetBranch,
                    sourceSha = request.SourceSha,
                    reviewRunId = request.ReviewRunId,
                    mergeRequestId = normalizedRequestId,
                    resultCode = "GitHubMergeEnqueued"
                })));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<DeviceRemoteLandingStatusResult>(
                    "The landing attempt changed while its GitHub enqueue status was being recorded. Refresh and retry.",
                    ErrorKind.Conflict);
            }
            catch (DbUpdateException)
            {
                return Result.Failure<DeviceRemoteLandingStatusResult>(
                    "The GitHub enqueue status could not be recorded because of a concurrent update.",
                    ErrorKind.Conflict);
            }
        }

        return Result.Ok(new DeviceRemoteLandingStatusResult(
            attempt.Id, attempt.ProjectId, attempt.TaskId, "enqueued", alreadyRecorded));
    }

    private static bool IsValidGitHubRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return false;
        var parts = repository.Split('/');
        return parts.Length == 2
            && parts.All(part => part.Length is > 0 and <= 100
                && part.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '_' or '-'));
    }

    private static bool IsCanonicalPullRequestUrl(string? url, string repository, int number)
    {
        var expected = $"https://github.com/{repository}/pull/{number}";
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parsed.AbsolutePath.TrimEnd('/'), $"/{repository}/pull/{number}", StringComparison.Ordinal)
            && string.IsNullOrEmpty(parsed.Query)
            && string.IsNullOrEmpty(parsed.Fragment)
            && string.Equals(url, expected, StringComparison.Ordinal);
    }

    public async Task<Result<DeviceRemoteLandingReconciliationResult>> HandleAsync(
        Guid deviceId,
        DeviceRemoteLandingReconciliationRequest request,
        CancellationToken ct = default)
    {
        if (deviceId == Guid.Empty || request.ProjectId == Guid.Empty
            || request.TaskId == Guid.Empty || request.ReviewRunId == Guid.Empty)
            return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                "Device, project, task, and review identifiers are required.", ErrorKind.Validation);

        if (request.VerifiedAtUtc > DateTime.UtcNow.AddMinutes(2)
            || request.MergedAtUtc > DateTime.UtcNow.AddMinutes(2)
            || request.MergedAtUtc == default || request.VerifiedAtUtc == default)
            return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                "GitHub merge and workstation verification timestamps are invalid.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await ProjectRuntimeMutationGate.TryLockProjectAsync(db, request.ProjectId, ct))
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    "Project not found.", ErrorKind.NotFound);

            var device = await db.DaemonDevices.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == deviceId, ct);
            if (device is null || device.IsRevoked
                || !WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.AutoSyncV1))
                return Result.Forbidden<DeviceRemoteLandingReconciliationResult>(
                    "An active AutoSync-capable device token is required.");

            var project = await db.Projects.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == request.ProjectId, ct);
            if (project is null)
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    "Project not found.", ErrorKind.NotFound);
            if (!ProjectGitPolicyCatalog.TryRead(
                project.GitPolicyJson, project.GitPolicyRevision, out var gitPolicy))
                return Result.Forbidden<DeviceRemoteLandingReconciliationResult>(
                    "The project Git policy is invalid; remote landing reconciliation is disabled.");

            if (!gitPolicy.AutoSyncEnabled
                && !WorkstationCapabilities.Supports(
                    device.ProbeJson, WorkstationCapabilities.RemoteLandingReconcileV1))
                return Result.Forbidden<DeviceRemoteLandingReconciliationResult>(
                    "This workstation does not advertise the AutoSync-independent remote landing protocol.");

            var runtimeAttached = await db.ProjectRuntimes.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(runtime => runtime.ProjectId == request.ProjectId
                    && runtime.DeviceId == deviceId, ct);
            if (!runtimeAttached)
                return Result.Forbidden<DeviceRemoteLandingReconciliationResult>(
                    "The device is not attached to the requested project.");

            var existing = await db.RemoteLandingProofs.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(proof => proof.ProjectId == request.ProjectId
                    && proof.TaskId == request.TaskId, ct);
            if (existing is not null)
            {
                if (Matches(existing, deviceId, request))
                    return Result.Ok(ToResult(existing, TaskItemStatus.Done, TaskPipelineStage.Closed, alreadyRecorded: true));
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    "A different remote landing proof is already recorded for this task.", ErrorKind.Conflict);
            }

            var task = await db.Tasks.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == request.TaskId
                    && item.ProjectId == request.ProjectId, ct);
            if (task is null)
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    "Task not found.", ErrorKind.NotFound);
            var releaseBlock = await ReleaseTaskMutationGuard.GetBlockReasonAsync(
                db, request.ProjectId, [task.MilestoneId], ct);
            if (releaseBlock is not null)
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    releaseBlock, ErrorKind.Conflict);
            if (task.PipelineDeviceId != deviceId)
                return Result.Forbidden<DeviceRemoteLandingReconciliationResult>(
                    "Only the workstation pinned to this task can report its remote landing.");
            if (task.Status != TaskItemStatus.InProgress
                || task.PipelineStage != TaskPipelineStage.Approved)
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    "The task must still be InProgress and pipeline-approved before a remote merge can close it.",
                    ErrorKind.Conflict);

            var reviewRun = await db.ReviewRuns.IgnoreQueryFilters().AsNoTracking()
                .Where(run => run.ProjectId == request.ProjectId && run.TaskId == request.TaskId)
                .OrderByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.Id)
                .FirstOrDefaultAsync(ct);
            if (reviewRun is null || reviewRun.Id != request.ReviewRunId
                || !reviewRun.IsLandingProof()
                || reviewRun.CheckedDeviceId != deviceId
                || !string.Equals(reviewRun.CheckedSourceSha, request.SourceSha, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(reviewRun.CheckedBaseBranch, request.TargetBranch, StringComparison.Ordinal))
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    "The reported merge does not match the latest successful Review proof for this pinned task workstation.",
                    ErrorKind.Conflict);

            var latestVerdict = await db.ReviewVerdicts.IgnoreQueryFilters().AsNoTracking()
                .Where(verdict => verdict.ProjectId == request.ProjectId && verdict.TaskId == request.TaskId)
                .OrderByDescending(verdict => verdict.CreatedAt)
                .ThenByDescending(verdict => verdict.Id)
                .FirstOrDefaultAsync(ct);
            if (latestVerdict is null || latestVerdict.Kind != ReviewVerdictKind.Approve)
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    "The latest pipeline review verdict must still be Approve.", ErrorKind.Conflict);

            // External/manual merges may be reconciled only when project AutoSync is enabled.
            // Beacon-managed async merges remain ExecutePending while GitHub processes a direct
            // merge or merge-queue entry. They become Landed only after the pinned workstation
            // proves the exact merge commit/tree and reruns the immutable project check.
            var latestLandingAttempt = await db.LandingAttempts.IgnoreQueryFilters()
                .Where(attempt => attempt.ProjectId == request.ProjectId && attempt.TaskId == request.TaskId)
                .OrderByDescending(attempt => attempt.CreatedAt)
                .ThenByDescending(attempt => attempt.Id)
                .FirstOrDefaultAsync(ct);
            if (latestLandingAttempt is not null)
            {
                var proofMatches = request.LandingAttemptId == latestLandingAttempt.Id
                    && latestLandingAttempt.ReviewRunId == request.ReviewRunId
                    && latestLandingAttempt.DeviceId == deviceId
                    && string.Equals(latestLandingAttempt.ReviewCommandHash, reviewRun.CheckCommandHash, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(latestLandingAttempt.SourceSha, request.SourceSha, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(latestLandingAttempt.TargetBranch, request.TargetBranch, StringComparison.Ordinal);
                if (!proofMatches)
                    return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                        "The remote merge does not match the latest Beacon landing attempt.", ErrorKind.Conflict);

                if (latestLandingAttempt.Status == LandingAttemptStatus.Landed)
                {
                    if (!string.Equals(latestLandingAttempt.MergeCommitSha, request.MergeCommitSha, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(latestLandingAttempt.CandidateTreeSha, request.CandidateTreeSha, StringComparison.OrdinalIgnoreCase)
                        || latestLandingAttempt.PostMergeCheckExitCode != 0
                        || request.PostMergeCheckExitCode != latestLandingAttempt.PostMergeCheckExitCode
                        || !string.Equals(
                            latestLandingAttempt.PostMergeCheckOutputHash,
                            request.PostMergeCheckOutputHash,
                            StringComparison.OrdinalIgnoreCase)
                        || !IsSha256(latestLandingAttempt.PostMergeCheckOutputHash))
                        return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                            "The GitHub merge candidate tree or post-merge evidence differs from the verified Beacon landing result.",
                            ErrorKind.Conflict);
                }
                else if (latestLandingAttempt.TryReadGitHubMergeRequestId(out var expectedMergeRequestId))
                {
                    if (!string.Equals(latestLandingAttempt.ReviewCommandHash, reviewRun.CheckCommandHash, StringComparison.OrdinalIgnoreCase))
                        return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                            "The queued landing attempt no longer matches the successful Review command proof.",
                            ErrorKind.Conflict);

                    var pinnedCheckCommand = project.ReviewCheckCommand?.Trim();
                    var currentCheckHash = string.IsNullOrWhiteSpace(pinnedCheckCommand)
                        ? null
                        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pinnedCheckCommand))).ToLowerInvariant();
                    if (!string.Equals(currentCheckHash, latestLandingAttempt.ReviewCommandHash, StringComparison.OrdinalIgnoreCase))
                        return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                            "The project's ReviewCheckCommand changed after the landing request. Restore the reviewed command and retry reconciliation.",
                            ErrorKind.Conflict);

                    if (request.LandingAttemptId != latestLandingAttempt.Id
                        || !string.Equals(request.MergeRequestId, expectedMergeRequestId, StringComparison.Ordinal)
                        || !string.Equals(request.CandidateTreeSha, latestLandingAttempt.CandidateTreeSha, StringComparison.OrdinalIgnoreCase)
                        || request.PostMergeCheckExitCode != 0
                        || !IsSha256(request.PostMergeCheckOutputHash)
                        || !IsGitSha(request.CandidateTreeSha)
                        || !IsGitSha(request.MergeCommitSha))
                        return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                            "The queued GitHub merge lacks exact async-request, candidate-tree or post-merge-check evidence.",
                            ErrorKind.Conflict);
                    try
                    {
                        latestLandingAttempt.RecordLanded(
                            request.MergeCommitSha, request.CandidateTreeSha!,
                            request.PostMergeCheckExitCode.Value, request.PostMergeCheckOutputHash!);
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                    {
                        return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                            "The queued GitHub merge evidence could not advance the landing attempt to Landed.",
                            ErrorKind.Conflict);
                    }
                }
                else
                {
                    return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                        "The Beacon landing attempt has not reached a verified Landed state for this exact GitHub merge. Resolve the landing attempt before closing the task.",
                        ErrorKind.Conflict);
                }
            }

            if (!gitPolicy.AutoSyncEnabled && latestLandingAttempt is null)
                return Result.Forbidden<DeviceRemoteLandingReconciliationResult>(
                    "AutoSync is disabled for this project. Only a Beacon-managed landing attempt with verified Landed evidence can be reconciled without AutoSync.");

            var activeSession = await db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(session => session.ProjectId == request.ProjectId
                    && session.TaskId == request.TaskId
                    && (session.Status == SessionStatus.Ready
                        || session.Status == SessionStatus.Active
                        || session.Status == SessionStatus.WaitingForUser), ct);
            if (activeSession)
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    "A pipeline session is still active or queued for this task. Remote landing was not recorded.",
                    ErrorKind.Conflict);

            RemoteLandingProof proof;
            try
            {
                proof = RemoteLandingProof.Create(
                    request.ProjectId, request.TaskId, deviceId, request.ReviewRunId,
                    request.Repository, request.PullRequestNumber, request.PullRequestUrl,
                    request.SourceBranch, request.TargetBranch, request.SourceSha,
                    request.MergeCommitSha, request.RemoteBaseSha,
                    request.MergedAtUtc, request.VerifiedAtUtc);
            }
            catch (ArgumentException exception)
            {
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    exception.Message, ErrorKind.Validation);
            }

            var mergeNote =
                $"Merged through GitHub PR #{proof.PullRequestNumber} ({proof.PullRequestUrl}).\n" +
                $"Reviewed task commit: {proof.SourceSha}\n" +
                $"GitHub merge commit: {proof.MergeCommitSha}\n" +
                $"Target branch: {proof.TargetBranch}\n" +
                $"Fetched origin/{proof.TargetBranch} at: {proof.RemoteBaseSha}\n" +
                $"GitHub merged at: {proof.MergedAtUtc:O}; verified by pinned workstation at: {proof.VerifiedAtUtc:O}.";
            try
            {
                task.ClosePipeline(mergeNote);
            }
            catch (InvalidOperationException exception)
            {
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    exception.Message, ErrorKind.Conflict);
            }
            catch (ArgumentException exception)
            {
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    exception.Message, ErrorKind.Validation);
            }

            db.RemoteLandingProofs.Add(proof);
            var actor = new ActorContext(
                ActorType.Device, null, false, null, deviceId,
                request.ProjectId, project.OrgId, ApiTokenCapability.None);
            db.AuditEvents.Add(AuditEventFactory.ForActor(
                actor,
                "task.remote_landing.reconciled",
                "RemoteLandingProof",
                proof.Id.ToString("D"),
                project.OrgId,
                request.ProjectId,
                detailsJson: JsonSerializer.Serialize(new
                {
                    deviceId,
                    sourceBranch = proof.SourceBranch,
                    sourceSha = proof.SourceSha,
                    targetBranch = proof.TargetBranch,
                    mergeCommitSha = proof.MergeCommitSha,
                    targetHeadSha = proof.RemoteBaseSha,
                    reviewRunId = proof.ReviewRunId,
                    resultCode = "TaskClosed"
                })));

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    "The task changed while the remote merge was being reconciled. Refresh and retry.", ErrorKind.Conflict);
            }
            catch (DbUpdateException)
            {
                return Result.Failure<DeviceRemoteLandingReconciliationResult>(
                    "A concurrent remote landing proof was recorded for this task. Read the task state before retrying.",
                    ErrorKind.Conflict);
            }

            await transaction.CommitAsync(ct);
            return Result.Ok(ToResult(proof, task.Status, task.PipelineStage ?? TaskPipelineStage.None, alreadyRecorded: false));

        });
    }

        });
    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool Matches(
        RemoteLandingProof proof,
        Guid deviceId,
        DeviceRemoteLandingReconciliationRequest request) =>
        proof.DeviceId == deviceId
        && proof.ReviewRunId == request.ReviewRunId
        && string.Equals(proof.GitHubRepository, request.Repository, StringComparison.OrdinalIgnoreCase)
        && proof.PullRequestNumber == request.PullRequestNumber
        && string.Equals(proof.PullRequestUrl, request.PullRequestUrl, StringComparison.Ordinal)
        && string.Equals(proof.SourceBranch, request.SourceBranch, StringComparison.Ordinal)
        && string.Equals(proof.TargetBranch, request.TargetBranch, StringComparison.Ordinal)
        && string.Equals(proof.SourceSha, request.SourceSha, StringComparison.OrdinalIgnoreCase)
        && string.Equals(proof.MergeCommitSha, request.MergeCommitSha, StringComparison.OrdinalIgnoreCase)
        && string.Equals(proof.RemoteBaseSha, request.RemoteBaseSha, StringComparison.OrdinalIgnoreCase);

    private static DeviceRemoteLandingReconciliationResult ToResult(
        RemoteLandingProof proof,
        TaskItemStatus status,
        TaskPipelineStage stage,
        bool alreadyRecorded) =>
        new(proof.Id, proof.ProjectId, proof.TaskId, proof.PullRequestNumber,
            proof.PullRequestUrl, proof.SourceSha, proof.MergeCommitSha,
            proof.TargetBranch, proof.RemoteBaseSha, status.ToString(), stage.ToString(), alreadyRecorded);
}
