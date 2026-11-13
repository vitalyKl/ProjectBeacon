namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Devices;
using Application.Persistence;
using Domain.Entities.Tasks;
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
        if (reviewRun is null || reviewRun.CreatedAt < latestVerdict.CreatedAt || !reviewRun.IsCheckProof()
            || !string.Equals(reviewRun.CheckCommandHash, expectedCommandHash, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<LandingAttemptDto>(
                "A successful review check for the current pinned command and task branch revision is required.", ErrorKind.Conflict);

        var pending = await db.LandingAttempts.AnyAsync(a =>
            a.ProjectId == task.ProjectId && a.TaskId == task.Id
            && (a.Status == LandingAttemptStatus.PreviewPending
                || a.Status == LandingAttemptStatus.Previewed
                || a.Status == LandingAttemptStatus.ExecutePending
                || a.Status == LandingAttemptStatus.RevertPending), ct);
        if (pending)
            return Result.Failure<LandingAttemptDto>("A landing attempt is already active for this task.", ErrorKind.Conflict);

        var attempt = LandingAttempt.Start(
            task.ProjectId, task.Id, reviewRun.Id, userId,
            reviewRun.CheckedSourceSha!, expectedCommandHash, rationale);
        db.LandingAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);

        var device = await FindLandingDeviceAsync(db, task.ProjectId, userId, ct);
        if (device is null)
        {
            attempt.Fail("NoGitLandWorkstation");
            await db.SaveChangesAsync(ct);
            return Result.Failure<LandingAttemptDto>(
                "No online project workstation advertises git-land-v1 and is owned by the requesting user.", ErrorKind.Conflict);
        }

        var payload = new JsonObject
        {
            ["projectId"] = task.ProjectId.ToString("D"),
            ["taskId"] = task.Id.ToString("D"),
            ["landingAttemptId"] = attempt.Id.ToString("D"),
            ["action"] = "Preview",
            ["sourceSha"] = attempt.SourceSha,
            ["checkCommandHash"] = attempt.ReviewCommandHash
        };
        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(
            new EnqueueCommandRequest(
                device.Id,
                userId,
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
        IBeaconDb db, Guid projectId, Guid userId, CancellationToken ct)
    {
        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.ProjectId == projectId && !string.IsNullOrWhiteSpace(r.LocalRoot))
            .ToListAsync(ct);
        if (runtimes.Count == 0)
            return null;
        var deviceIds = runtimes.Select(r => r.DeviceId).Distinct().ToArray();
        var devices = await db.DaemonDevices.IgnoreQueryFilters()
            .Where(d => deviceIds.Contains(d.Id) && d.UserId == userId && d.RevokedAt == null)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        return devices
            .Where(d => d.IsOnline(now) && WorkstationCapabilities.Supports(d.ProbeJson, WorkstationCapabilities.GitLandV1))
            .OrderByDescending(d => d.LastHeartbeatAt)
            .FirstOrDefault();
    }

    internal static LandingAttemptDto ToDto(LandingAttempt attempt) => new(
        attempt.Id, attempt.TaskId, attempt.Status, attempt.SourceBranch, attempt.SourceSha,
        attempt.TargetBranch, attempt.TargetHeadSha, attempt.CandidateTreeSha,
        attempt.MergeCommitSha, attempt.RevertCommitSha, attempt.FailureCode,
        attempt.ConflictFilesJson, attempt.CreatedAt, attempt.UpdatedAt);
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
        if (attempt.Status != LandingAttemptStatus.Landed || string.IsNullOrWhiteSpace(attempt.MergeCommitSha))
            return Result.Failure<LandingAttemptDto>("Only a landed attempt can be reverted.", ErrorKind.Conflict);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == attempt.ProjectId, ct);
        if (project is null || string.IsNullOrWhiteSpace(project.ReviewCheckCommand))
            return Result.Failure<LandingAttemptDto>("The pinned project check command is unavailable.", ErrorKind.Conflict);
        var device = await RequestLandingHandler.FindLandingDeviceAsync(db, attempt.ProjectId, userId, ct);
        if (device is null)
            return Result.Failure<LandingAttemptDto>("No online GitLand-capable project workstation is available.", ErrorKind.Conflict);

        attempt.MarkRevertPending();
        await db.SaveChangesAsync(ct);

        var payload = new JsonObject
        {
            ["projectId"] = attempt.ProjectId.ToString("D"),
            ["taskId"] = attempt.TaskId.ToString("D"),
            ["landingAttemptId"] = attempt.Id.ToString("D"),
            ["action"] = "Revert",
            ["sourceSha"] = attempt.SourceSha,
            ["checkCommandHash"] = attempt.ReviewCommandHash,
            ["mergeCommitSha"] = attempt.MergeCommitSha
        };
        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(
            new EnqueueCommandRequest(
                device.Id, userId, WorkstationCommandKind.GitLand, payload.ToJsonString(),
                attempt.ProjectId, project.ReviewCheckCommand.Trim())
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
                previousState = LandingAttemptStatus.Landed.ToString(),
                newState = attempt.Status.ToString(),
                resultCode = "RevertQueued"
            })));
        await db.SaveChangesAsync(ct);
        return Result.Ok(RequestLandingHandler.ToDto(attempt));
    }
}
