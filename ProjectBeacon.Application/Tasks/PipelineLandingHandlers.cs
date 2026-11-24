namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Devices;
using Application.Persistence;
using Application.Projects;
using Domain.Entities.Tasks;
using Domain.Entities.Projects;
using Domain.Common;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Server-side request to preview a task branch landing.</summary>
public sealed class RequestLandingHandler : ICommandHandler<RequestLandingCommand, Result<LandingAttemptDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;

    public RequestLandingHandler(IBeaconDbFactory dbFactory, EnqueueCommandHandler enqueue)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
    }

    public async Task<Result<LandingAttemptDto>> HandleAsync(RequestLandingCommand command, CancellationToken ct = default)
    {
        var rationale = command.Request.Rationale?.Trim();
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > 1000)
            return Result.Failure<LandingAttemptDto>("Rationale must contain 1 to 1000 characters.", ErrorKind.Validation);
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<LandingAttemptDto>("Task not found.", ErrorKind.NotFound);
        if (!LandingAuthorizationPolicy.CanRequestLanding(command.Actor, task.ProjectId))
            return Result.Forbidden<LandingAttemptDto>(
                "A user-bound project member or a project-scoped API token with TaskWrite capability is required to request a landing.");

        if (task.PipelineStage != TaskPipelineStage.Approved)
            return Result.Failure<LandingAttemptDto>("The task pipeline must be Approved before requesting a landing.", ErrorKind.Conflict);

        // Project is org-scoped while this handler runs in a project-scoped tenant
        // context. The task above was resolved by that scope; this exact ProjectId lookup
        // is required to read the pinned ReviewCheckCommand and does not broaden access.
        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == task.ProjectId, ct);
        if (project is null || string.IsNullOrWhiteSpace(project.ReviewCheckCommand))
            return Result.Failure<LandingAttemptDto>("A pinned project ReviewCheckCommand is required before landing.", ErrorKind.Conflict);
        var checkCommand = project.ReviewCheckCommand.Trim();
        if (!ProjectGitPolicyCatalog.TryRead(project.GitPolicyJson, project.GitPolicyRevision, out var gitPolicy))
            return Result.Failure<LandingAttemptDto>(
                "The project's Git policy is invalid. Landing automation remains disabled until an Owner/Admin repairs it.",
                ErrorKind.Conflict);
        if (!ProjectVersionPolicyCatalog.TryRead(project.VersionPolicyJson, out var versionPolicy, out var versionPolicyError))
            return Result.Failure<LandingAttemptDto>(
                "The project's version policy is invalid. Landing automation remains disabled until an Owner/Admin repairs it. " + versionPolicyError,
                ErrorKind.Conflict);
        // Configured version manifests are server-protected from ordinary Actor landings.
        // The exact list is copied into the immutable landing attempt so policy edits cannot
        // silently change the files protected by an already requested review/landing.
        gitPolicy = gitPolicy with
        {
            ProtectedPaths = gitPolicy.ProtectedPaths
                .Concat(versionPolicy.Sources.Select(source => source.Path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray()
        };
        var expectedCommandHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();

        var latestVerdict = await db.ReviewVerdicts
            .Where(v => v.TaskId == task.Id && v.ProjectId == task.ProjectId)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(ct);
        if (latestVerdict is null || latestVerdict.Kind != ReviewVerdictKind.Approve)
            return Result.Failure<LandingAttemptDto>("The latest review verdict must be Approve.", ErrorKind.Conflict);

        var reviewRun = await db.ReviewRuns
            .Where(r => r.TaskId == task.Id && r.ProjectId == task.ProjectId)
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (reviewRun is null || !reviewRun.IsLandingProof()
            || reviewRun.StartedAt < latestVerdict.CreatedAt
            || !string.Equals(reviewRun.CheckCommandHash, expectedCommandHash, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(reviewRun.CheckedBaseBranch))
            return Result.Failure<LandingAttemptDto>(
                "The latest review run must have a successful check for the current approval, pinned command, task branch revision, and base branch.", ErrorKind.Conflict);

        // Landing proof must originate from the same workstation that owns the task's
        // persistent branch. A valid SHA/tree from another checkout is not sufficient.
        var workstation = await ProjectGitSyncPreflight.ResolveTaskWorkstationAsync(
            db, task, DateTime.UtcNow, ct);
        if (workstation.BlockedReason is not null || workstation.Device is null)
            return Result.Failure<LandingAttemptDto>(
                ProjectGitSyncPreflight.WorkstationBlockedMessage(workstation.BlockedReason),
                ErrorKind.Conflict);
        if (reviewRun.CheckedDeviceId != workstation.Device.Id)
            return Result.Failure<LandingAttemptDto>(
                "The review proof was produced on a workstation other than the one pinned to this task. Rerun the Review Check on the task's pinned workstation.",
                ErrorKind.Conflict);
        if (!await ProjectGitSyncPreflight.TryPinTaskWorkstationAsync(
                db, task.Id, workstation.Device.Id, ct))
            return Result.Failure<LandingAttemptDto>(
                "The task was pinned to a different workstation concurrently. Reload the task and retry landing.",
                ErrorKind.Conflict);

        var reviewBaseBranch = reviewRun.CheckedBaseBranch!;
        string landingTargetBranch;
        if (task.HotfixSourceReleaseId is not null)
        {
            if (string.IsNullOrWhiteSpace(task.HotfixTargetBranch)
                || !string.Equals(reviewBaseBranch, task.HotfixTargetBranch, StringComparison.Ordinal))
                return Result.Failure<LandingAttemptDto>(
                    "A Hotfix review proof must target the exact production branch recorded by its published Release.",
                    ErrorKind.Conflict);
            if (gitPolicy.AutonomyLevel == 1
                || string.Equals(gitPolicy.DirtyCheckoutBehavior, "LandViaIntegration", StringComparison.Ordinal))
                return Result.Failure<LandingAttemptDto>(
                    "Hotfix landing must target the deployed production branch directly. Disable integration-branch landing/fallback in Project Git Settings before requesting the Hotfix landing.",
                    ErrorKind.Conflict);
            landingTargetBranch = task.HotfixTargetBranch;
        }
        else
        {
            landingTargetBranch = gitPolicy.AutonomyLevel == 1
                ? ProjectGitPolicyCatalog.IntegrationBranchName
                : reviewBaseBranch;
        }
        var policySnapshot = ProjectGitPolicyCatalog.SerializeAttemptSnapshot(gitPolicy, reviewBaseBranch);

        var latestLanding = await db.LandingAttempts
            .Where(attempt => attempt.ProjectId == task.ProjectId && attempt.TaskId == task.Id)
            .OrderByDescending(attempt => attempt.CreatedAt)
            .ThenByDescending(attempt => attempt.Id)
            .FirstOrDefaultAsync(ct);
        if (latestLanding?.Status == LandingAttemptStatus.Conflicted
            && (latestLanding.PreviewedAt is null
                || reviewRun.StartedAt <= latestLanding.PreviewedAt.Value
                || string.Equals(reviewRun.CheckedSourceSha, latestLanding.SourceSha, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure<LandingAttemptDto>(
                "The task branch has not been integrated and reviewed since the conflicted preview. Integrate the daemon-selected target into the task branch, start a fresh Review, record a new Approve verdict, and rerun the pinned review check before requesting another landing.",
                ErrorKind.Conflict);

        var latestProofMatches = latestLanding is not null
            && latestLanding.ReviewRunId == reviewRun.Id
            && string.Equals(latestLanding.SourceSha, reviewRun.CheckedSourceSha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(latestLanding.ReviewTreeSha, reviewRun.CheckedTreeSha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(latestLanding.ReviewCommandHash, expectedCommandHash, StringComparison.OrdinalIgnoreCase)
            && ProjectGitPolicyCatalog.TryReadReviewBaseBranch(
                latestLanding.GitPolicySnapshotJson, latestLanding.TargetBranch, out var previousReviewBase)
            && string.Equals(previousReviewBase, reviewBaseBranch, StringComparison.Ordinal)
            && latestLanding.DeviceId == reviewRun.CheckedDeviceId;

        if (latestLanding?.Status == LandingAttemptStatus.PreviewPending)
        {
            if (latestLanding.GitPolicyRevision != gitPolicy.Revision)
                return Result.Failure<LandingAttemptDto>(
                    "The project Git policy changed while Preview was pending. Let that command finish or recover before requesting a new Preview.",
                    ErrorKind.Conflict);
            if (!latestProofMatches)
                return Result.Failure<LandingAttemptDto>(
                    "A Preview is already pending for a different review proof. Inspect its landing status before starting another attempt.",
                    ErrorKind.Conflict);
            var previewDevice = await FindLandingDeviceAsync(
                db, task.ProjectId, latestLanding.DeviceId, ct,
                requireIntegrationBranch: gitPolicy.AutonomyLevel == 1
                    || gitPolicy.DirtyCheckoutBehavior == "LandViaIntegration");
            if (previewDevice is null)
                return Result.Failure<LandingAttemptDto>(
                    "The workstation assigned to the pending Preview is offline; reconnect it and retry the same landing request.",
                    ErrorKind.Conflict);
            return await QueuePreviewAsync(db, latestLanding, previewDevice, checkCommand, command.Actor,
                project: null, dispatchAlreadyClaimed: false, ct);
        }

        if (latestLanding?.Status == LandingAttemptStatus.Previewed)
        {
            if (latestProofMatches && latestLanding.GitPolicyRevision == gitPolicy.Revision)
                return Result.Ok(ToDto(latestLanding));
            if (latestProofMatches && latestLanding.GitPolicyRevision != gitPolicy.Revision)
            {
                latestLanding.Fail("GitPolicyChangedSincePreview");
                latestLanding.SetEscalationCode("GitPolicyChangedSincePreview");
                await db.SaveChangesAsync(ct);
            }
            else
            {
                return Result.Failure<LandingAttemptDto>(
                    "A successful Preview already exists for a different review proof. Execute or resolve that attempt before requesting another landing.",
                    ErrorKind.Conflict);
            }
        }

        if (latestLanding is not null && IsLandingAttemptBlockingNewPreview(latestLanding.Status))
            return Result.Failure<LandingAttemptDto>(
                $"Landing attempt {latestLanding.Id:D} is still {latestLanding.Status}; resolve it before requesting another Preview.",
                ErrorKind.Conflict);

        if (reviewRun.CheckedDeviceId is not { } reviewedDeviceId || reviewedDeviceId == Guid.Empty)
            return Result.Failure<LandingAttemptDto>("The review proof does not identify its workstation.", ErrorKind.Conflict);
        var device = await FindLandingDeviceAsync(
            db, task.ProjectId, reviewedDeviceId, ct,
            requireIntegrationBranch: gitPolicy.AutonomyLevel == 1
                || gitPolicy.DirtyCheckoutBehavior == "LandViaIntegration");
        if (device is null)
            return Result.Failure<LandingAttemptDto>(
                "The workstation that produced the review proof is offline or no longer GitLand-capable; rerun the review on an eligible workstation.",
                ErrorKind.Conflict);

        if (string.IsNullOrWhiteSpace(reviewRun.CheckedBaseBranch))
            return Result.Failure<LandingAttemptDto>("The review proof does not identify its daemon-selected target branch.", ErrorKind.Conflict);

        var attempt = LandingAttempt.Start(
            task.ProjectId, task.Id, reviewRun.Id, command.Actor.UserId!.Value, device.Id, landingTargetBranch,
            reviewRun.CheckedSourceSha!, reviewRun.CheckedTreeSha!, expectedCommandHash, rationale);
        attempt.SetGitPolicySnapshot(gitPolicy.Revision, policySnapshot);
        attempt.MarkDispatchPending("Preview");
        db.LandingAttempts.Add(attempt);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            var concurrent = await db.LandingAttempts
                .Where(candidate => candidate.ProjectId == task.ProjectId && candidate.TaskId == task.Id)
                .OrderByDescending(candidate => candidate.CreatedAt)
                .ThenByDescending(candidate => candidate.Id)
                .FirstOrDefaultAsync(ct);
            if (concurrent?.Status == LandingAttemptStatus.PreviewPending
                && concurrent.ReviewRunId == reviewRun.Id
                && concurrent.DeviceId == device.Id
                && string.Equals(concurrent.SourceSha, reviewRun.CheckedSourceSha, StringComparison.OrdinalIgnoreCase)
                && string.Equals(concurrent.ReviewTreeSha, reviewRun.CheckedTreeSha, StringComparison.OrdinalIgnoreCase)
                && string.Equals(concurrent.ReviewCommandHash, expectedCommandHash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(concurrent.TargetBranch, landingTargetBranch, StringComparison.Ordinal)
                && ProjectGitPolicyCatalog.TryReadReviewBaseBranch(
                    concurrent.GitPolicySnapshotJson, concurrent.TargetBranch, out var concurrentReviewBase)
                && string.Equals(concurrentReviewBase, reviewBaseBranch, StringComparison.Ordinal))
                return Result.Ok(ToDto(concurrent));
            return Result.Failure<LandingAttemptDto>(
                "Another landing attempt was created for this task; refresh the landing state and retry.", ErrorKind.Conflict);
        }

        return await QueuePreviewAsync(db, attempt, device, checkCommand, command.Actor,
            project, dispatchAlreadyClaimed: true, ct);
    }

    private static bool IsLandingAttemptBlockingNewPreview(LandingAttemptStatus status) =>
        status is LandingAttemptStatus.ExecutePending
            or LandingAttemptStatus.Landed
            or LandingAttemptStatus.LandedUnverified
            or LandingAttemptStatus.RevertPending
            or LandingAttemptStatus.RevertedUnverified
            or LandingAttemptStatus.RevertFailed;

    private async Task<Result<LandingAttemptDto>> QueuePreviewAsync(
        IBeaconDb db,
        LandingAttempt attempt,
        Domain.Entities.Devices.DaemonDevice device,
        string checkCommand,
        ActorContext actor,
        Domain.Entities.Projects.Project? project,
        bool dispatchAlreadyClaimed,
        CancellationToken ct)
    {
        if (!dispatchAlreadyClaimed)
        {
            var activeCommand = await LandingCommandDispatch.FindActiveCommandAsync(
                db, attempt.ProjectId, attempt.Id, "Preview", ct);
            if (activeCommand is not null || LandingCommandDispatch.HasFreshLease(attempt, "Preview"))
                return Result.Ok(ToDto(attempt));

            if (!await LandingCommandDispatch.TryClaimAsync(db, attempt, "Preview", ct))
            {
                await db.Entry(attempt).ReloadAsync(ct);
                return Result.Ok(ToDto(attempt));
            }
            await db.Entry(attempt).ReloadAsync(ct);
        }

        if (!ProjectGitPolicyCatalog.TryRead(
                attempt.GitPolicySnapshotJson, attempt.GitPolicyRevision, out var attemptPolicy))
        {
            attempt.Fail("InvalidGitPolicySnapshot");
            await db.SaveChangesAsync(ct);
            return Result.Failure<LandingAttemptDto>("The landing attempt contains an invalid Git policy snapshot.", ErrorKind.Conflict);
        }
        var payload = new JsonObject
        {
            ["projectId"] = attempt.ProjectId.ToString("D"),
            ["taskId"] = attempt.TaskId.ToString("D"),
            ["landingAttemptId"] = attempt.Id.ToString("D"),
            ["action"] = "Preview",
            ["targetBranch"] = attempt.TargetBranch,
            ["reviewBaseBranch"] = ProjectGitPolicyCatalog.TryReadReviewBaseBranch(
                attempt.GitPolicySnapshotJson, attempt.TargetBranch, out var reviewBase) ? reviewBase : attempt.TargetBranch,
            ["allowIntegrationFallback"] = attemptPolicy.AutonomyLevel != 1
                && attemptPolicy.DirtyCheckoutBehavior == "LandViaIntegration",
            ["sourceSha"] = attempt.SourceSha,
            ["reviewTreeSha"] = attempt.ReviewTreeSha,
            ["checkCommandHash"] = attempt.ReviewCommandHash,
            ["maxChangedFiles"] = attemptPolicy.MaxChangedFiles,
            ["maxChangedLines"] = attemptPolicy.MaxChangedLines,
            ["protectedPaths"] = JsonSerializer.SerializeToNode(attemptPolicy.ProtectedPaths)
        };
        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(
            new EnqueueCommandRequest(
                device.Id, device.UserId, WorkstationCommandKind.GitLand,
                payload.ToJsonString(), attempt.ProjectId, checkCommand)
            {
                TrustedGitLand = true
            }), ct);
        if (!queued.Success)
        {
            attempt.Fail("EnqueueFailed");
            await db.SaveChangesAsync(ct);
            return Result.Failure<LandingAttemptDto>(queued.Error ?? "Could not queue GitLand Preview.");
        }

        await LandingCommandDispatch.ClearLeaseAsync(db, attempt, "Preview", ct);
        await db.Entry(attempt).ReloadAsync(ct);

        var auditProject = project
            ?? await db.Projects.IgnoreQueryFilters()
                .FirstOrDefaultAsync(candidate => candidate.Id == attempt.ProjectId, ct);
        if (auditProject is not null)
        {
            db.AuditEvents.Add(AuditEventFactory.ForActor(
                actor,
                "pipeline.landing.preview_requested",
                "LandingAttempt",
                attempt.Id.ToString("D"),
                auditProject.OrgId,
                auditProject.Id,
                detailsJson: JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["previousState"] = "None",
                    ["newState"] = attempt.Status.ToString(),
                    ["resultCode"] = "PreviewQueued",
                    ["sourceBranch"] = attempt.SourceBranch,
                    ["targetBranch"] = attempt.TargetBranch,
                    ["sourceSha"] = attempt.SourceSha,
                    ["reviewTreeSha"] = attempt.ReviewTreeSha,
                    ["reviewCommandHash"] = attempt.ReviewCommandHash,
                    ["reviewRunId"] = attempt.ReviewRunId.ToString("D"),
                    ["deviceId"] = attempt.DeviceId.ToString("D")
                })));
            await db.SaveChangesAsync(ct);
        }

        return Result.Ok(ToDto(attempt));
    }

    internal static async Task<Domain.Entities.Devices.DaemonDevice?> FindLandingDeviceAsync(
        IBeaconDb db, Guid projectId, Guid deviceId, CancellationToken ct, bool requireIntegrationBranch = false)
    {
        if (deviceId == Guid.Empty)
            return null;
        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == projectId && r.DeviceId == deviceId
                && !string.IsNullOrWhiteSpace(r.LocalRoot))
            .ToListAsync(ct);
        if (runtimes.Count == 0)
            return null;
        var devices = await db.DaemonDevices.IgnoreQueryFilters()
            .Where(d => d.Id == deviceId && d.RevokedAt == null)
            .ToListAsync(ct);
        if (devices.Count == 0)
            return null;
        var ownerIds = devices.Select(d => d.UserId).Distinct().ToArray();
        var projectMemberIds = await db.ProjectMembers.IgnoreQueryFilters()
            .Where(member => member.ProjectId == projectId && ownerIds.Contains(member.UserId))
            .Select(member => member.UserId)
            .Distinct()
            .ToArrayAsync(ct);
        var eligibleOwners = projectMemberIds.ToHashSet();
        var now = DateTime.UtcNow;
        return devices
            .Where(d => eligibleOwners.Contains(d.UserId)
                && d.IsOnline(now)
                && WorkstationCapabilities.Supports(d.ProbeJson, WorkstationCapabilities.GitLandV1)
                && (!requireIntegrationBranch
                    || WorkstationCapabilities.Supports(d.ProbeJson, WorkstationCapabilities.GitLandIntegrationV1)))
            .OrderByDescending(d => d.LastHeartbeatAt)
            .ThenBy(d => d.Id)
            .FirstOrDefault();
    }

    internal static LandingAttemptDto ToDto(LandingAttempt attempt) => new(
        attempt.Id, attempt.TaskId, attempt.Status, attempt.SourceBranch, attempt.SourceSha,
        attempt.ReviewTreeSha, attempt.DeviceId, attempt.TargetBranch, attempt.TargetHeadSha, attempt.CandidateTreeSha,
        attempt.MergeCommitSha, attempt.RevertCommitSha, LandingCommandDispatch.PublicFailureCode(attempt),
        attempt.ConflictFilesJson, attempt.PostMergeCheckExitCode, attempt.PostMergeCheckOutputHash,
        attempt.CreatedAt, attempt.UpdatedAt, attempt.EscalationCode, attempt.GitPolicyRevision);
}

/// <summary>Reads one landing attempt scoped to the requested task.</summary>
public sealed class GetLandingAttemptHandler : ICommandHandler<GetLandingAttemptCommand, Result<LandingAttemptDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetLandingAttemptHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<LandingAttemptDto>> HandleAsync(GetLandingAttemptCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var attempt = await db.LandingAttempts.FirstOrDefaultAsync(a =>
            a.TaskId == command.Request.TaskId && a.Id == command.Request.LandingAttemptId, ct);
        return attempt is null
            ? Result.Failure<LandingAttemptDto>("Landing attempt not found.", ErrorKind.NotFound)
            : Result.Ok(RequestLandingHandler.ToDto(attempt));
    }
}

/// <summary>Queues an Execute operation only for the still-current successful preview and review proof.</summary>
public sealed class ExecuteLandingHandler : ICommandHandler<ExecuteLandingCommand, Result<LandingAttemptDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;

    public ExecuteLandingHandler(IBeaconDbFactory dbFactory, EnqueueCommandHandler enqueue)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
    }

    public async Task<Result<LandingAttemptDto>> HandleAsync(ExecuteLandingCommand command, CancellationToken ct = default)
    {
        if (command.Request.Automatic)
        {
            if (!command.Actor.IsWorker)
                return Result.Forbidden<LandingAttemptDto>("Only the trusted worker can request automatic landing execution.");
        }
        else if (!command.Actor.IsHuman || command.Actor.UserId is not { } userId || userId == Guid.Empty)
        {
            return Result.Forbidden<LandingAttemptDto>("A human user-bound actor is required to execute a landing.");
        }

        await using var db = _dbFactory.CreateDbContext();
        var attempt = await db.LandingAttempts.FirstOrDefaultAsync(a =>
            a.Id == command.Request.LandingAttemptId && a.TaskId == command.Request.TaskId, ct);
        if (attempt is null)
            return Result.Failure<LandingAttemptDto>("Landing attempt not found.", ErrorKind.NotFound);
        var activeCommand = await LandingCommandDispatch.FindActiveCommandAsync(
            db, attempt.ProjectId, attempt.Id, "Execute", ct);
        if (activeCommand is not null || LandingCommandDispatch.HasFreshLease(attempt, "Execute"))
            return Result.Ok(RequestLandingHandler.ToDto(attempt));

        if (attempt.Status is not (LandingAttemptStatus.Previewed or LandingAttemptStatus.ExecutePending)
            || string.IsNullOrWhiteSpace(attempt.TargetBranch)
            || string.IsNullOrWhiteSpace(attempt.TargetHeadSha)
            || string.IsNullOrWhiteSpace(attempt.CandidateTreeSha)
            || attempt.CandidateCheckExitCode != 0)
            return Result.Failure<LandingAttemptDto>(
                "Execute requires a successful preview or an unacknowledged Execute that can be reconciled.", ErrorKind.Conflict);

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == attempt.TaskId && t.ProjectId == attempt.ProjectId, ct);
        if (task is null || task.PipelineStage != TaskPipelineStage.Approved)
        {
            await InvalidateUnqueuedExecuteAsync(db, attempt, "TaskNotApprovedForExecute", ct);
            return Result.Failure<LandingAttemptDto>("The task must remain Approved until Execute is queued.", ErrorKind.Conflict);
        }

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == attempt.ProjectId, ct);
        var checkCommand = project?.ReviewCheckCommand?.Trim();
        if (string.IsNullOrWhiteSpace(checkCommand))
        {
            await InvalidateUnqueuedExecuteAsync(db, attempt, "ReviewCommandUnavailable", ct);
            return Result.Failure<LandingAttemptDto>("The pinned project ReviewCheckCommand is unavailable.", ErrorKind.Conflict);
        }
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        if (!string.Equals(commandHash, attempt.ReviewCommandHash, StringComparison.OrdinalIgnoreCase))
        {
            await InvalidateUnqueuedExecuteAsync(db, attempt, "ReviewCommandChanged", ct);
            return Result.Failure<LandingAttemptDto>("The project's review check command changed after Preview.", ErrorKind.Conflict);
        }
        if (!ProjectGitPolicyCatalog.TryRead(project!.GitPolicyJson, project.GitPolicyRevision, out var currentPolicy)
            || !ProjectGitPolicyCatalog.TryRead(attempt.GitPolicySnapshotJson, attempt.GitPolicyRevision, out var attemptPolicy))
        {
            await InvalidateUnqueuedExecuteAsync(db, attempt, "InvalidGitPolicySnapshot", ct);
            return Result.Failure<LandingAttemptDto>("The project's Git policy snapshot is invalid.", ErrorKind.Conflict);
        }
        if (project.GitPolicyRevision != attempt.GitPolicyRevision)
        {
            await InvalidateUnqueuedExecuteAsync(db, attempt, "GitPolicyChangedSincePreview", ct);
            return Result.Failure<LandingAttemptDto>(
                "Project Git policy changed after Preview. Request a fresh Preview before executing this attempt.", ErrorKind.Conflict);
        }
        if (command.Request.Automatic && currentPolicy.AutonomyLevel is not (1 or 2))
        {
            await InvalidateUnqueuedExecuteAsync(db, attempt, "AutomaticLandingNotAllowed", ct);
            return Result.Failure<LandingAttemptDto>("Automatic landing is allowed only at autonomy level 1 or 2.", ErrorKind.Forbidden);
        }

        var latestVerdict = await db.ReviewVerdicts
            .Where(v => v.TaskId == attempt.TaskId && v.ProjectId == attempt.ProjectId)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(ct);
        if (latestVerdict is null || latestVerdict.Kind != ReviewVerdictKind.Approve)
        {
            await InvalidateUnqueuedExecuteAsync(db, attempt, "ReviewApprovalChanged", ct);
            return Result.Failure<LandingAttemptDto>("The latest review verdict must still be Approve.", ErrorKind.Conflict);
        }

        var reviewRun = await db.ReviewRuns.FirstOrDefaultAsync(r =>
            r.Id == attempt.ReviewRunId && r.TaskId == attempt.TaskId && r.ProjectId == attempt.ProjectId, ct);
        var latestReviewRunId = await db.ReviewRuns
            .Where(r => r.TaskId == attempt.TaskId && r.ProjectId == attempt.ProjectId)
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (reviewRun is null || !reviewRun.IsLandingProof()
            || latestReviewRunId != attempt.ReviewRunId
            || reviewRun.StartedAt < latestVerdict.CreatedAt
            || !string.Equals(reviewRun.CheckedSourceSha, attempt.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reviewRun.CheckedTreeSha, attempt.ReviewTreeSha, StringComparison.OrdinalIgnoreCase)
            || !ProjectGitPolicyCatalog.TryReadReviewBaseBranch(
                attempt.GitPolicySnapshotJson, attempt.TargetBranch, out var pinnedReviewBaseBranch)
            || !string.Equals(reviewRun.CheckedBaseBranch, pinnedReviewBaseBranch, StringComparison.Ordinal)
            || reviewRun.CheckedDeviceId != attempt.DeviceId
            || !string.Equals(reviewRun.CheckCommandHash, attempt.ReviewCommandHash, StringComparison.OrdinalIgnoreCase))
        {
            await InvalidateUnqueuedExecuteAsync(db, attempt, "ReviewProofChanged", ct);
            return Result.Failure<LandingAttemptDto>("The latest review proof no longer matches this landing attempt.", ErrorKind.Conflict);
        }

        var device = await RequestLandingHandler.FindLandingDeviceAsync(
            db, attempt.ProjectId, attempt.DeviceId, ct,
            requireIntegrationBranch: attemptPolicy.AutonomyLevel == 1
                || attemptPolicy.DirtyCheckoutBehavior == "LandViaIntegration");
        if (device is null)
        {
            await InvalidateUnqueuedExecuteAsync(db, attempt, "LandingWorkstationUnavailable", ct);
            return Result.Failure<LandingAttemptDto>("No online GitLand-capable project workstation is available.", ErrorKind.Conflict);
        }

        var previousState = attempt.Status;
        var claimed = await LandingCommandDispatch.TryClaimAsync(db, attempt, "Execute", ct);
        if (!claimed)
        {
            await db.Entry(attempt).ReloadAsync(ct);
            return Result.Ok(RequestLandingHandler.ToDto(attempt));
        }
        await db.Entry(attempt).ReloadAsync(ct);
        var payload = new JsonObject
        {
            ["projectId"] = attempt.ProjectId.ToString("D"),
            ["taskId"] = attempt.TaskId.ToString("D"),
            ["landingAttemptId"] = attempt.Id.ToString("D"),
            ["action"] = "Execute",
            ["targetBranch"] = attempt.TargetBranch,
            ["reviewBaseBranch"] = ProjectGitPolicyCatalog.TryReadReviewBaseBranch(
                attempt.GitPolicySnapshotJson, attempt.TargetBranch, out var executeReviewBase) ? executeReviewBase : attempt.TargetBranch,
            ["allowIntegrationFallback"] = false,
            ["sourceSha"] = attempt.SourceSha,
            ["reviewTreeSha"] = attempt.ReviewTreeSha,
            ["checkCommandHash"] = attempt.ReviewCommandHash,
            ["targetHeadSha"] = attempt.TargetHeadSha,
            ["targetTreeSha"] = attempt.CandidateTreeSha,
            ["maxChangedFiles"] = attemptPolicy.MaxChangedFiles,
            ["maxChangedLines"] = attemptPolicy.MaxChangedLines,
            ["protectedPaths"] = JsonSerializer.SerializeToNode(attemptPolicy.ProtectedPaths)
        };
        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(
            new EnqueueCommandRequest(
                device.Id, device.UserId, WorkstationCommandKind.GitLand,
                payload.ToJsonString(), attempt.ProjectId, checkCommand)
            {
                TrustedGitLand = true,
                TrustedGitPolicyRevision = attempt.GitPolicyRevision
            }), ct);
        if (!queued.Success)
        {
            // EnqueueCommandHandler returns a failed Result only when no durable command
            // was created. This is not an unknown completion: release ExecutePending so
            // the task can request a fresh Preview instead of waiting forever.
            await InvalidateUnqueuedExecuteAsync(db, attempt, "ExecuteEnqueueFailed", ct);
            return Result.Failure<LandingAttemptDto>(queued.Error ?? "Could not queue GitLand Execute.");
        }

        await LandingCommandDispatch.ClearLeaseAsync(db, attempt, "Execute", ct);
        await db.Entry(attempt).ReloadAsync(ct);
        db.AuditEvents.Add(AuditEventFactory.ForActor(
            command.Actor,
            "pipeline.landing.execute_requested",
            "LandingAttempt",
            attempt.Id.ToString("D"),
            project!.OrgId,
            project.Id,
            detailsJson: JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["previousState"] = previousState.ToString(),
                ["newState"] = attempt.Status.ToString(),
                ["resultCode"] = previousState == LandingAttemptStatus.ExecutePending ? "ExecuteRetryQueued" : "ExecuteQueued",
                ["sourceBranch"] = attempt.SourceBranch,
                ["targetBranch"] = attempt.TargetBranch,
                ["sourceSha"] = attempt.SourceSha,
                ["reviewTreeSha"] = attempt.ReviewTreeSha,
                ["reviewCommandHash"] = attempt.ReviewCommandHash,
                ["targetHeadSha"] = attempt.TargetHeadSha!,
                ["candidateTreeSha"] = attempt.CandidateTreeSha!,
                ["deviceId"] = attempt.DeviceId.ToString("D")
            })));
        await db.SaveChangesAsync(ct);
        return Result.Ok(RequestLandingHandler.ToDto(attempt));
    }

    private static async Task InvalidateUnqueuedExecuteAsync(
        IBeaconDb db,
        LandingAttempt attempt,
        string failureCode,
        CancellationToken ct)
    {
        if (attempt.Status is LandingAttemptStatus.Previewed or LandingAttemptStatus.ExecutePending)
            attempt.Fail(failureCode);
        attempt.SetEscalationCode(failureCode);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Server-side request to revert a landing while the target still points at its merge commit.</summary>
public sealed class RevertLandingHandler : ICommandHandler<RevertLandingCommand, Result<LandingAttemptDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;

    public RevertLandingHandler(IBeaconDbFactory dbFactory, EnqueueCommandHandler enqueue)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
    }

    public async Task<Result<LandingAttemptDto>> HandleAsync(RevertLandingCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var attempt = await db.LandingAttempts.FirstOrDefaultAsync(a =>
            a.Id == command.Request.LandingAttemptId && a.TaskId == command.Request.TaskId, ct);
        if (attempt is null)
            return Result.Failure<LandingAttemptDto>("Landing attempt not found.");
        if (!LandingAuthorizationPolicy.CanRequestRevert(
                command.Actor, attempt.ProjectId, command.Request.Automatic))
            return Result.Forbidden<LandingAttemptDto>(
                "Revert requires a human caller or a project-bound TaskWrite API token. Automatic reverts are worker-only.");
        var activeCommand = await LandingCommandDispatch.FindActiveCommandAsync(
            db, attempt.ProjectId, attempt.Id, "Revert", ct);
        if (activeCommand is not null || LandingCommandDispatch.HasFreshLease(attempt, "Revert"))
            return Result.Ok(RequestLandingHandler.ToDto(attempt));

        if (attempt.Status is not (LandingAttemptStatus.Landed
            or LandingAttemptStatus.LandedUnverified
            or LandingAttemptStatus.RevertPending
            or LandingAttemptStatus.RevertFailed)
            || string.IsNullOrWhiteSpace(attempt.MergeCommitSha))
            return Result.Failure<LandingAttemptDto>(
                "Revert requires a landed attempt or a retryable/uncertain Revert with a recorded merge commit.", ErrorKind.Conflict);

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == attempt.ProjectId, ct);
        if (project is null)
            return Result.Failure<LandingAttemptDto>("Project not found.", ErrorKind.NotFound);
        var device = await RequestLandingHandler.FindLandingDeviceAsync(
            db, attempt.ProjectId, attempt.DeviceId, ct,
            requireIntegrationBranch: string.Equals(
                attempt.TargetBranch, ProjectGitPolicyCatalog.IntegrationBranchName, StringComparison.Ordinal));
        if (device is null)
            return Result.Failure<LandingAttemptDto>("No online GitLand-capable project workstation is available.", ErrorKind.Conflict);

        if (!ProjectGitPolicyCatalog.TryRead(attempt.GitPolicySnapshotJson, attempt.GitPolicyRevision, out var policySnapshot))
            return Result.Failure<LandingAttemptDto>("The landing attempt contains an invalid Git policy snapshot.", ErrorKind.Conflict);

        var previousState = attempt.Status;
        var claimed = await LandingCommandDispatch.TryClaimAsync(db, attempt, "Revert", ct);
        if (!claimed)
        {
            await db.Entry(attempt).ReloadAsync(ct);
            return Result.Ok(RequestLandingHandler.ToDto(attempt));
        }
        await db.Entry(attempt).ReloadAsync(ct);

        var payload = new JsonObject
        {
            ["projectId"] = attempt.ProjectId.ToString("D"),
            ["taskId"] = attempt.TaskId.ToString("D"),
            ["landingAttemptId"] = attempt.Id.ToString("D"),
            ["action"] = "Revert",
            ["targetBranch"] = attempt.TargetBranch,
            ["reviewBaseBranch"] = ProjectGitPolicyCatalog.TryReadReviewBaseBranch(
                attempt.GitPolicySnapshotJson, attempt.TargetBranch, out var revertReviewBase) ? revertReviewBase : attempt.TargetBranch,
            ["allowIntegrationFallback"] = false,
            ["sourceSha"] = attempt.SourceSha,
            ["reviewTreeSha"] = attempt.ReviewTreeSha,
            ["checkCommandHash"] = attempt.ReviewCommandHash,
            ["mergeCommitSha"] = attempt.MergeCommitSha,
            ["maxChangedFiles"] = policySnapshot.MaxChangedFiles,
            ["maxChangedLines"] = policySnapshot.MaxChangedLines,
            ["protectedPaths"] = JsonSerializer.SerializeToNode(policySnapshot.ProtectedPaths)
        };
        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(
            new EnqueueCommandRequest(
                device.Id, device.UserId, WorkstationCommandKind.GitLand, payload.ToJsonString(),
                attempt.ProjectId)
            {
                TrustedGitLand = true
            }), ct);
        if (!queued.Success)
        {
            attempt.MarkCommandCompletionUnknown();
            await db.SaveChangesAsync(ct);
            return Result.Failure<LandingAttemptDto>(queued.Error ?? "Could not queue GitLand Revert.");
        }

        await LandingCommandDispatch.ClearLeaseAsync(db, attempt, "Revert", ct);
        await db.Entry(attempt).ReloadAsync(ct);
        db.AuditEvents.Add(AuditEventFactory.ForActor(
            command.Actor,
            "pipeline.landing.revert_requested",
            "LandingAttempt",
            attempt.Id.ToString("D"),
            project.OrgId,
            project.Id,
            detailsJson: JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["previousState"] = previousState.ToString(),
                ["newState"] = attempt.Status.ToString(),
                ["resultCode"] = previousState is LandingAttemptStatus.RevertPending or LandingAttemptStatus.RevertFailed
                    ? "RevertRetryQueued" : "RevertQueued",
                ["sourceBranch"] = attempt.SourceBranch,
                ["targetBranch"] = attempt.TargetBranch,
                ["sourceSha"] = attempt.SourceSha,
                ["reviewTreeSha"] = attempt.ReviewTreeSha,
                ["reviewCommandHash"] = attempt.ReviewCommandHash,
                ["mergeCommitSha"] = attempt.MergeCommitSha!,
                ["deviceId"] = attempt.DeviceId.ToString("D")
            })));
        await db.SaveChangesAsync(ct);
        return Result.Ok(RequestLandingHandler.ToDto(attempt));
    }
}

/// <summary>Authorization boundary for a request to revert a task landing.</summary>
public static class LandingAuthorizationPolicy
{
    public static bool CanRequestLanding(ActorContext actor, Guid projectId)
    {
        if (projectId == Guid.Empty
            || actor.UserId is not { } userId
            || userId == Guid.Empty)
            return false;

        if (actor.IsHuman)
            return actor.ProjectId is null || actor.ProjectId == projectId;

        return actor.IsApiToken
            && actor.ProjectId == projectId
            && (actor.Capabilities & ApiTokenCapability.TaskWrite) == ApiTokenCapability.TaskWrite;
    }

    public static bool CanRequestRevert(ActorContext actor, Guid projectId, bool automatic)
    {
        if (projectId == Guid.Empty)
            return false;

        if (automatic)
            return actor.IsWorker && actor.ProjectId == projectId;

        if (actor.UserId is not { } userId || userId == Guid.Empty)
            return false;

        if (actor.IsHuman)
            return actor.ProjectId is null || actor.ProjectId == projectId;

        return actor.IsApiToken
            && actor.ProjectId == projectId
            && (actor.Capabilities & ApiTokenCapability.TaskWrite) == ApiTokenCapability.TaskWrite;
    }
}
