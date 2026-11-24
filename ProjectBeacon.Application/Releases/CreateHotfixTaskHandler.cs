namespace ProjectBeacon.Application.Releases;

using System.Text.Json;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Application.Tasks;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Creates an ordinary Beacon task whose persistent task branch is pinned to the exact
/// commit that the published Release deployment endpoint verified in production.
/// </summary>
public sealed class CreateHotfixTaskHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;
    private readonly ContentWriter _contentWriter;

    public CreateHotfixTaskHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter auditWriter,
        ContentWriter? contentWriter = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
        _contentWriter = contentWriter ?? new ContentWriter();
    }

    public async Task<Result<TaskItemDto>> HandleAsync(
        CreateHotfixTaskCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        var title = request.Title?.Trim() ?? string.Empty;
        var description = request.Description?.Trim();

        if (request.ProjectId == Guid.Empty || request.ReleaseId == Guid.Empty
            || request.RequestId == Guid.Empty || title.Length is < 1 or > 500)
            return Result.Failure<TaskItemDto>(
                "Project, published Release, idempotency key, and a task title of 1-500 characters are required.",
                ErrorKind.Validation);
        if (description?.Length > 5000)
            return Result.Failure<TaskItemDto>(
                "Hotfix description cannot exceed 5000 characters.", ErrorKind.Validation);
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<TaskItemDto>(
                "Only a human project Owner/Admin can create a Hotfix task.");

        await using var db = _dbFactory.CreateDbContext();
        var permission = await ReleaseAuthorization.RequireAdminAsync(
            db, _authorization, request.ProjectId, command.Actor, ct);
        if (!permission.Success)
            return Result.Forbidden<TaskItemDto>(
                "Only a project or organization Owner/Admin can create a Hotfix task.");

        var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, request.ProjectId, ct);
        if (!enabled.Success)
            return Result.Failure<TaskItemDto>(
                enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

        var existing = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(
            task => task.ProjectId == request.ProjectId && task.HotfixRequestId == request.RequestId, ct);
        if (existing is not null)
        {
            if (existing.HotfixSourceReleaseId != request.ReleaseId)
                return Result.Failure<TaskItemDto>(
                    "This Hotfix request id was already used for another Release.", ErrorKind.Conflict);
            return Result.Ok(ToDto(existing));
        }

        var release = await db.Releases.FirstOrDefaultAsync(
            item => item.ProjectId == request.ProjectId && item.Id == request.ReleaseId, ct);
        if (release is null)
            return Result.Failure<TaskItemDto>("Release not found.", ErrorKind.NotFound);

        if (release.Status != ReleaseStatus.Published
            || release.PublicationStatus != ReleasePublicationStatus.Published
            || !release.PublicationTagPushed
            || release.DeployedAtUtc is null
            || release.DeploymentWorkflowRunId is not > 0
            || string.IsNullOrWhiteSpace(release.DeploymentWorkflowRunUrl))
            return Result.Failure<TaskItemDto>(
                "Hotfix can only start from a Release whose production publication was independently verified.",
                ErrorKind.Conflict);

        var currentDeployedReleaseId = await db.Releases.AsNoTracking()
            .Where(item => item.ProjectId == request.ProjectId
                && item.Status == ReleaseStatus.Published
                && item.PublicationStatus == ReleasePublicationStatus.Published
                && item.PublicationTagPushed
                && item.DeployedAtUtc != null
                && item.DeploymentWorkflowRunId != null)
            .OrderByDescending(item => item.DeployedAtUtc)
            .ThenByDescending(item => item.PublishedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => item.Id)
            .FirstOrDefaultAsync(ct);
        if (currentDeployedReleaseId != release.Id)
            return Result.Failure<TaskItemDto>(
                "This Release is no longer the current production deployment. Create the Hotfix from the most recently verified deployed Release.",
                ErrorKind.Conflict);

        if (release.PreparationDeviceId is not { } pinnedDeviceId
            || release.CandidateId is not { } candidateId)
            return Result.Failure<TaskItemDto>(
                "The published Release has no immutable Candidate or preparation workstation.", ErrorKind.Conflict);

        var candidate = await db.ReleaseCandidateSnapshots.AsNoTracking().FirstOrDefaultAsync(
            item => item.ProjectId == request.ProjectId
                && item.ReleaseId == release.Id
                && item.CandidateId == candidateId
                && item.PreparedByDeviceId == pinnedDeviceId, ct);
        if (candidate is null
            || string.IsNullOrWhiteSpace(release.DeployedCommitSha)
            || !IsGitSha(release.DeployedCommitSha)
            || !string.Equals(release.DeployedCommitSha, candidate.CandidateSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(release.DeployedVersion, candidate.Version, StringComparison.Ordinal)
            || !string.Equals(release.PublishedTagName, candidate.TagName, StringComparison.Ordinal)
            || !string.Equals(release.TargetBranch, candidate.TargetBranch, StringComparison.Ordinal)
            || !ProjectVersionPolicyCatalog.IsSafeBranchName(candidate.TargetBranch))
            return Result.Failure<TaskItemDto>(
                "The production deployment proof does not match the immutable Candidate version, tag, branch and SHA.",
                ErrorKind.Conflict);

        var runtime = await db.ProjectRuntimes.AsNoTracking().FirstOrDefaultAsync(
            item => item.ProjectId == request.ProjectId && item.DeviceId == pinnedDeviceId, ct);
        var device = await db.DaemonDevices.AsNoTracking().FirstOrDefaultAsync(
            item => item.Id == pinnedDeviceId && item.UserId == command.Actor.UserId, ct);
        if (runtime is null || string.IsNullOrWhiteSpace(runtime.LocalRoot)
            || device is null || device.IsRevoked
            || !WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.GitLandV1))
            return Result.Failure<TaskItemDto>(
                "The workstation pinned to the deployed Release is no longer available to this project owner or does not support verified landing. Reconnect it; Beacon will not silently switch the Hotfix to another machine.",
                ErrorKind.Conflict);

        var task = TaskItem.Create(
            title, request.ProjectId, TaskPriority.High, TaskType.Bug, description);
        task.SetHotfixOrigin(
            release.Id, release.DeployedCommitSha, candidate.TargetBranch, request.RequestId);
        task.PinPipelineDevice(pinnedDeviceId);
        db.Tasks.Add(task);

        var attribution = ContentAttribution.FromActor(command.Actor);
        _contentWriter.AddVersion(
            db, task, attribution.Type ?? "Human", attribution.Id);

        _auditWriter.Stage(db, AuditEventFactory.ForActor(
            command.Actor,
            "project.release.hotfix_task_created",
            "Task",
            task.Id.ToString("D"),
            await ReleaseAuthorization.GetOrgIdAsync(db, request.ProjectId, ct),
            request.ProjectId,
            detailsJson: JsonSerializer.Serialize(new
            {
                releaseId = release.Id.ToString("D"),
                deployedSha = release.DeployedCommitSha,
                targetBranch = candidate.TargetBranch,
                deviceId = pinnedDeviceId.ToString("D"),
                hotfixRequestId = request.RequestId.ToString("D")
            })));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<TaskItemDto>(
                "Release or project task state changed concurrently. Refresh and retry.", ErrorKind.Conflict);
        }
        catch (DbUpdateException)
        {
            // The unique HotfixRequestId index is the final guard against duplicate
            // submissions arriving concurrently. Re-read the winner in a fresh context.
            await using var fresh = _dbFactory.CreateDbContext();
            var winner = await fresh.Tasks.AsNoTracking().FirstOrDefaultAsync(
                item => item.ProjectId == request.ProjectId && item.HotfixRequestId == request.RequestId, ct);
            if (winner is not null && winner.HotfixSourceReleaseId == request.ReleaseId)
                return Result.Ok(ToDto(winner));
            throw;
        }

        return Result.Ok(ToDto(task));
    }

    private static TaskItemDto ToDto(TaskItem task) => new(
        task.Id, task.Title, task.Description, task.Status.ToString(),
        task.Priority, task.Type, null, task.ProjectId, task.LabelId,
        task.MilestoneId, task.ReviewNotes, task.CreatedAt, task.CompletedAt,
        [], [], task.ContentRevision, task.HotfixSourceReleaseId,
        task.HotfixDeployedSha, task.HotfixTargetBranch, task.HotfixRequestId,
        task.ForwardPortOfTaskId);

    private static bool IsGitSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}

public sealed record CreateHotfixTaskRequest(
    string Title,
    string? Description,
    Guid RequestId);

public sealed record CreateHotfixTaskCommand(
    Guid ProjectId,
    Guid ReleaseId,
    CreateHotfixTaskRequest Request,
    ActorContext Actor);
