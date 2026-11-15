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
using Domain.Entities.Tasks;
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
        if (command.Actor.UserId is not { } userId || userId == Guid.Empty)
            return Result.Failure<LandingAttemptDto>("A user-bound actor is required to request a landing.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<LandingAttemptDto>("Task not found.");

        if (task.PipelineStage != TaskPipelineStage.Approved)
            return Result.Failure<LandingAttemptDto>("The task pipeline must be Approved before requesting a landing.", ErrorKind.Conflict);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == task.ProjectId, ct);
        if (project is null || string.IsNullOrWhiteSpace(project.ReviewCheckCommand))
            return Result.Failure<LandingAttemptDto>("A pinned project ReviewCheckCommand is required before landing.", ErrorKind.Conflict);
        var checkCommand = project.ReviewCheckCommand.Trim();
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
            .Where(r => r.TaskId == task.Id && r.ProjectId == task.ProjectId
                && r.Status == ReviewRunStatus.Completed)
            .OrderByDescending(r => r.CompletedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (reviewRun is null || !reviewRun.IsCheckProof()
            || !string.Equals(reviewRun.CheckCommandHash, expectedCommandHash, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<LandingAttemptDto>(
                "A successful review check for the current pinned command and task branch revision is required.", ErrorKind.Conflict);

        var pending = await db.LandingAttempts.AnyAsync(a =>
            a.ProjectId == task.ProjectId && a.TaskId == task.Id
            && (a.Status == LandingAttemptStatus.PreviewPending
                || a.Status == LandingAttemptStatus.Previewed
                || a.Status == LandingAttemptStatus.ExecutePending
                || a.Status == LandingAttemptStatus.Landed
                || a.Status == LandingAttemptStatus.LandedUnverified
                || a.Status == LandingAttemptStatus.RevertPending), ct);
        if (pending)
            return Result.Failure<LandingAttemptDto>("A landing attempt is already active for this task.", ErrorKind.Conflict);

        if (reviewRun.CheckedDeviceId is not { } reviewedDeviceId || reviewedDeviceId == Guid.Empty)
            return Result.Failure<LandingAttemptDto>("The review proof does not identify its workstation.", ErrorKind.Conflict);
        var device = await FindLandingDeviceAsync(db, task.ProjectId, reviewedDeviceId, ct);
        if (device is null)
            return Result.Failure<LandingAttemptDto>(
                "The workstation that produced the review proof is offline or no longer GitLand-capable; rerun the review on an eligible workstation.",
                ErrorKind.Conflict);

        if (string.IsNullOrWhiteSpace(reviewRun.CheckedBaseBranch))
            return Result.Failure<LandingAttemptDto>("The review proof does not identify its daemon-selected target branch.", ErrorKind.Conflict);

        var attempt = LandingAttempt.Start(
            task.ProjectId, task.Id, reviewRun.Id, userId, device.Id, reviewRun.CheckedBaseBranch,
            reviewRun.CheckedSourceSha!, reviewRun.CheckedTreeSha!, expectedCommandHash, rationale);
        db.LandingAttempts.Add(attempt);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<LandingAttemptDto>(
                "Another landing attempt was created for this task; refresh the landing state and retry.", ErrorKind.Conflict);
        }

        var payload = new JsonObject
        {
            ["projectId"] = task.ProjectId.ToString("D"),
            ["taskId"] = task.Id.ToString("D"),
            ["landingAttemptId"] = attempt.Id.ToString("D"),
            ["action"] = "Preview",
            ["targetBranch"] = attempt.TargetBranch,
            ["sourceSha"] = attempt.SourceSha,
            ["reviewTreeSha"] = attempt.ReviewTreeSha,
            ["checkCommandHash"] = attempt.ReviewCommandHash
        };
        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(
            new EnqueueCommandRequest(
                device.Id,
                device.UserId,
                WorkstationCommandKind.GitLand,
                payload.ToJsonString(),
                task.ProjectId,
                checkCommand)
            {
                TrustedGitLand = true
            }), ct);
        if (!queued.Success)
        {
            attempt.Fail("EnqueueFailed");
            await db.SaveChangesAsync(ct);
            return Result.Failure<LandingAttemptDto>(queued.Error ?? "Could not queue GitLand Preview.");
        }

        db.AuditEvents.Add(AuditEventFactory.ForActor(
            command.Actor,
            "pipeline.landing.preview_requested",
            "LandingAttempt",
            attempt.Id.ToString("D"),
            project.OrgId,
            project.Id,
            detailsJson: JsonSerializer.Serialize(new
            {
                newState = attempt.Status.ToString(),
                resultCode = "PreviewQueued"
            })));
        await db.SaveChangesAsync(ct);
        return Result.Ok(ToDto(attempt));
    }

    internal static async Task<Domain.Entities.Devices.DaemonDevice?> FindLandingDeviceAsync(
        IBeaconDb db, Guid projectId, Guid deviceId, CancellationToken ct)
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
                && WorkstationCapabilities.Supports(d.ProbeJson, WorkstationCapabilities.GitLandV1))
            .OrderByDescending(d => d.LastHeartbeatAt)
            .ThenBy(d => d.Id)
            .FirstOrDefault();
    }

    internal static LandingAttemptDto ToDto(LandingAttempt attempt) => new(
        attempt.Id, attempt.TaskId, attempt.Status, attempt.SourceBranch, attempt.SourceSha,
        attempt.ReviewTreeSha, attempt.DeviceId, attempt.TargetBranch, attempt.TargetHeadSha, attempt.CandidateTreeSha,
        attempt.MergeCommitSha, attempt.RevertCommitSha, attempt.FailureCode,
        attempt.ConflictFilesJson, attempt.PostMergeCheckExitCode, attempt.PostMergeCheckOutputHash,
        attempt.CreatedAt, attempt.UpdatedAt);
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
        if (command.Actor.UserId is not { } userId || userId == Guid.Empty)
            return Result.Failure<LandingAttemptDto>("A user-bound actor is required to execute a landing.");

        await using var db = _dbFactory.CreateDbContext();
        var attempt = await db.LandingAttempts.FirstOrDefaultAsync(a =>
            a.Id == command.Request.LandingAttemptId && a.TaskId == command.Request.TaskId, ct);
        if (attempt is null)
            return Result.Failure<LandingAttemptDto>("Landing attempt not found.", ErrorKind.NotFound);
        if (attempt.Status != LandingAttemptStatus.Previewed
            || string.IsNullOrWhiteSpace(attempt.TargetBranch)
            || string.IsNullOrWhiteSpace(attempt.TargetHeadSha)
            || string.IsNullOrWhiteSpace(attempt.CandidateTreeSha)
            || attempt.CandidateCheckExitCode != 0)
            return Result.Failure<LandingAttemptDto>("Only a successful, complete preview can be executed.", ErrorKind.Conflict);

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == attempt.TaskId && t.ProjectId == attempt.ProjectId, ct);
        if (task is null || task.PipelineStage != TaskPipelineStage.Approved)
            return Result.Failure<LandingAttemptDto>("The task must remain Approved until Execute is queued.", ErrorKind.Conflict);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == attempt.ProjectId, ct);
        var checkCommand = project?.ReviewCheckCommand?.Trim();
        if (string.IsNullOrWhiteSpace(checkCommand))
            return Result.Failure<LandingAttemptDto>("The pinned project ReviewCheckCommand is unavailable.", ErrorKind.Conflict);
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        if (!string.Equals(commandHash, attempt.ReviewCommandHash, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<LandingAttemptDto>("The project's review check command changed after Preview.", ErrorKind.Conflict);

        var reviewRun = await db.ReviewRuns.FirstOrDefaultAsync(r =>
            r.Id == attempt.ReviewRunId && r.TaskId == attempt.TaskId && r.ProjectId == attempt.ProjectId, ct);
        if (reviewRun is null || !reviewRun.IsCheckProof()
            || !string.Equals(reviewRun.CheckedSourceSha, attempt.SourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reviewRun.CheckedTreeSha, attempt.ReviewTreeSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reviewRun.CheckedBaseBranch, attempt.TargetBranch, StringComparison.Ordinal)
            || !string.Equals(reviewRun.CheckCommandHash, attempt.ReviewCommandHash, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<LandingAttemptDto>("The review proof no longer matches this landing attempt.", ErrorKind.Conflict);

        var latestVerdict = await db.ReviewVerdicts
            .Where(v => v.TaskId == attempt.TaskId && v.ProjectId == attempt.ProjectId)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(ct);
        if (latestVerdict is null || latestVerdict.Kind != ReviewVerdictKind.Approve)
            return Result.Failure<LandingAttemptDto>("The latest review verdict must still be Approve.", ErrorKind.Conflict);

        var device = await RequestLandingHandler.FindLandingDeviceAsync(db, attempt.ProjectId, attempt.DeviceId, ct);
        if (device is null)
            return Result.Failure<LandingAttemptDto>("No online GitLand-capable project workstation is available.", ErrorKind.Conflict);

        var claimed = await db.LandingAttempts
            .Where(candidate => candidate.Id == attempt.Id
                && candidate.Status == LandingAttemptStatus.Previewed)
            .ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.Status, LandingAttemptStatus.ExecutePending)
                .SetProperty(candidate => candidate.UpdatedAt, DateTime.UtcNow)
                .SetProperty(candidate => candidate.FailureCode, (string?)null), ct);
        if (claimed != 1)
            return Result.Failure<LandingAttemptDto>("The landing preview was already claimed by another execution request.", ErrorKind.Conflict);
        await db.Entry(attempt).ReloadAsync(ct);
        var payload = new JsonObject
        {
            ["projectId"] = attempt.ProjectId.ToString("D"),
            ["taskId"] = attempt.TaskId.ToString("D"),
            ["landingAttemptId"] = attempt.Id.ToString("D"),
            ["action"] = "Execute",
            ["targetBranch"] = attempt.TargetBranch,
            ["sourceSha"] = attempt.SourceSha,
            ["reviewTreeSha"] = attempt.ReviewTreeSha,
            ["checkCommandHash"] = attempt.ReviewCommandHash,
            ["targetHeadSha"] = attempt.TargetHeadSha,
            ["targetTreeSha"] = attempt.CandidateTreeSha
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
            return Result.Failure<LandingAttemptDto>(queued.Error ?? "Could not queue GitLand Execute.");
        }

        db.AuditEvents.Add(AuditEventFactory.ForActor(
            command.Actor,
            "pipeline.landing.execute_requested",
            "LandingAttempt",
            attempt.Id.ToString("D"),
            project!.OrgId,
            project.Id,
            detailsJson: JsonSerializer.Serialize(new
            {
                previousState = LandingAttemptStatus.Previewed.ToString(),
                newState = attempt.Status.ToString(),
                resultCode = "ExecuteQueued"
            })));
        await db.SaveChangesAsync(ct);
        return Result.Ok(RequestLandingHandler.ToDto(attempt));
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
        if (command.Actor.UserId is not { } userId || userId == Guid.Empty)
            return Result.Failure<LandingAttemptDto>("A user-bound actor is required to request a revert.");
        await using var db = _dbFactory.CreateDbContext();
        var attempt = await db.LandingAttempts.FirstOrDefaultAsync(a =>
            a.Id == command.Request.LandingAttemptId && a.TaskId == command.Request.TaskId, ct);
        if (attempt is null)
            return Result.Failure<LandingAttemptDto>("Landing attempt not found.");
        if (attempt.Status is not (LandingAttemptStatus.Landed or LandingAttemptStatus.LandedUnverified)
            || string.IsNullOrWhiteSpace(attempt.MergeCommitSha))
            return Result.Failure<LandingAttemptDto>("Only a landed attempt can be reverted.", ErrorKind.Conflict);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == attempt.ProjectId, ct);
        if (project is null)
            return Result.Failure<LandingAttemptDto>("Project not found.", ErrorKind.NotFound);
        var device = await RequestLandingHandler.FindLandingDeviceAsync(db, attempt.ProjectId, attempt.DeviceId, ct);
        if (device is null)
            return Result.Failure<LandingAttemptDto>("No online GitLand-capable project workstation is available.", ErrorKind.Conflict);

        var previousState = attempt.Status;
        var claimed = await db.LandingAttempts
            .Where(candidate => candidate.Id == attempt.Id
                && (candidate.Status == LandingAttemptStatus.Landed
                    || candidate.Status == LandingAttemptStatus.LandedUnverified))
            .ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.Status, LandingAttemptStatus.RevertPending)
                .SetProperty(candidate => candidate.UpdatedAt, DateTime.UtcNow), ct);
        if (claimed != 1)
            return Result.Failure<LandingAttemptDto>("The landing attempt was already claimed by another revert request.", ErrorKind.Conflict);
        await db.Entry(attempt).ReloadAsync(ct);

        var payload = new JsonObject
        {
            ["projectId"] = attempt.ProjectId.ToString("D"),
            ["taskId"] = attempt.TaskId.ToString("D"),
            ["landingAttemptId"] = attempt.Id.ToString("D"),
            ["action"] = "Revert",
            ["targetBranch"] = attempt.TargetBranch,
            ["sourceSha"] = attempt.SourceSha,
            ["reviewTreeSha"] = attempt.ReviewTreeSha,
            ["checkCommandHash"] = attempt.ReviewCommandHash,
            ["mergeCommitSha"] = attempt.MergeCommitSha
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
            attempt.Fail("EnqueueFailed");
            await db.SaveChangesAsync(ct);
            return Result.Failure<LandingAttemptDto>(queued.Error ?? "Could not queue GitLand Revert.");
        }

        db.AuditEvents.Add(AuditEventFactory.ForActor(
            command.Actor,
            "pipeline.landing.revert_requested",
            "LandingAttempt",
            attempt.Id.ToString("D"),
            project.OrgId,
            project.Id,
            detailsJson: JsonSerializer.Serialize(new
            {
                previousState = previousState.ToString(),
                newState = attempt.Status.ToString(),
                resultCode = "RevertQueued"
            })));
        await db.SaveChangesAsync(ct);
        return Result.Ok(RequestLandingHandler.ToDto(attempt));
    }
}
