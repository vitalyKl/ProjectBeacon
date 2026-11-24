namespace ProjectBeacon.Application.Releases;

using System.Text.Json;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

internal static class ReleaseAuthorization
{
    public static async Task<bool> CanReadProjectAsync(
        IBeaconDb db, Guid projectId, ActorContext actor, CancellationToken ct)
    {
        if (actor.IsAdmin)
            return true;

        if (actor.IsApiToken)
            return (actor.Capabilities.HasFlag(ApiTokenCapability.Admin)
                    || actor.Capabilities.HasFlag(ApiTokenCapability.TaskRead))
                && actor.ProjectId == projectId;

        if (!actor.IsHuman || actor.UserId is null)
            return false;

        if (await db.ProjectMembers.AnyAsync(
            member => member.ProjectId == projectId && member.UserId == actor.UserId, ct))
            return true;

        var orgId = await db.Projects
            .Where(project => project.Id == projectId)
            .Select(project => (Guid?)project.OrgId)
            .FirstOrDefaultAsync(ct);
        return orgId is not null && await db.OrgMembers.AnyAsync(
            member => member.OrgId == orgId && member.UserId == actor.UserId, ct);
    }

    public static async Task<Result<ProjectVersionPolicy>> RequireEnabledAsync(
        IBeaconDb db, Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == projectId, ct);
        if (project is null)
            return Result.Failure<ProjectVersionPolicy>("Project not found.", ErrorKind.NotFound);

        if (!ProjectVersionPolicyCatalog.TryRead(project.VersionPolicyJson, out var policy, out var error))
            return Result.Failure<ProjectVersionPolicy>(
                error ?? "Stored project version policy is invalid.", ErrorKind.Conflict);
        if (!policy.ReleaseManagementEnabled)
            return Result.Failure<ProjectVersionPolicy>(
                "Release Management is disabled for this project. An Owner/Admin can enable it in Project Settings.",
                ErrorKind.Conflict);
        return Result.Ok(policy);
    }

    public static async Task<Result> RequireAdminAsync(
        IBeaconDb db, IAuthorizationService authorization, Guid projectId,
        ActorContext actor, CancellationToken ct)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden("Only a human project Owner/Admin can manage releases.");
        return await authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
    }

    public static async Task<Guid?> GetOrgIdAsync(
        IBeaconDb db, Guid projectId, CancellationToken ct) =>
        await db.Projects.Where(project => project.Id == projectId)
            .Select(project => (Guid?)project.OrgId)
            .FirstOrDefaultAsync(ct);
}

internal static class ReleaseMapping
{
    public static ReleaseDto ToDto(Release release, int milestoneCount) =>
        new(
            release.Id, release.ProjectId, release.Name, release.Description,
            release.TargetVersion, release.TargetBranch, release.Status, release.Order,
            release.CreatedAt, release.UpdatedAt, release.PublishedAt, release.Revision,
            milestoneCount, release.PreparationStatus, release.PreparationRevision,
            release.PreparationDeviceId, release.CandidateId, release.PreparationCommandId,
            release.VerificationCommandId, release.PreparationResultJson, release.PreparationError);
}

public sealed class ListProjectReleasesHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListProjectReleasesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IReadOnlyList<ReleaseDto>>> HandleAsync(
        ListProjectReleasesCommand command, CancellationToken ct = default)
    {
        var projectId = command.Request.ProjectId;
        if (projectId == Guid.Empty)
            return Result.Failure<IReadOnlyList<ReleaseDto>>("Project id is required.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        if (!await ReleaseAuthorization.CanReadProjectAsync(db, projectId, command.Actor, ct))
            return Result.Forbidden<IReadOnlyList<ReleaseDto>>();
        var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, projectId, ct);
        if (!enabled.Success)
            return Result.Failure<IReadOnlyList<ReleaseDto>>(enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

        var releases = await db.Releases.AsNoTracking()
            .Where(release => release.ProjectId == projectId)
            .OrderBy(release => release.Order)
            .ThenBy(release => release.CreatedAt)
            .Select(release => new ReleaseDto(
                release.Id, release.ProjectId, release.Name, release.Description,
                release.TargetVersion, release.TargetBranch, release.Status, release.Order,
                release.CreatedAt, release.UpdatedAt, release.PublishedAt, release.Revision,
                release.Milestones.Count, release.PreparationStatus, release.PreparationRevision,
                release.PreparationDeviceId, release.CandidateId, release.PreparationCommandId,
                release.VerificationCommandId, release.PreparationResultJson, release.PreparationError))
            .ToArrayAsync(ct);
        return Result.Ok<IReadOnlyList<ReleaseDto>>(releases);
    }
}

public sealed class GetReleaseHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetReleaseHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ReleaseDto>> HandleAsync(
        GetReleaseCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (request.ReleaseId == Guid.Empty || request.ProjectId == Guid.Empty)
            return Result.Failure<ReleaseDto>("Release and project ids are required.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        if (!await ReleaseAuthorization.CanReadProjectAsync(db, request.ProjectId, command.Actor, ct))
            return Result.Forbidden<ReleaseDto>();
        var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, request.ProjectId, ct);
        if (!enabled.Success)
            return Result.Failure<ReleaseDto>(enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

        var dto = await db.Releases.AsNoTracking()
            .Where(release => release.Id == request.ReleaseId && release.ProjectId == request.ProjectId)
            .Select(release => new ReleaseDto(
                release.Id, release.ProjectId, release.Name, release.Description,
                release.TargetVersion, release.TargetBranch, release.Status, release.Order,
                release.CreatedAt, release.UpdatedAt, release.PublishedAt, release.Revision,
                release.Milestones.Count, release.PreparationStatus, release.PreparationRevision,
                release.PreparationDeviceId, release.CandidateId, release.PreparationCommandId,
                release.VerificationCommandId, release.PreparationResultJson, release.PreparationError))
            .FirstOrDefaultAsync(ct);
        return dto is null
            ? Result.Failure<ReleaseDto>("Release not found.", ErrorKind.NotFound)
            : Result.Ok(dto);
    }
}

public sealed class CreateReleaseHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;

    public CreateReleaseHandler(
        IBeaconDbFactory dbFactory, IAuthorizationService authorization, IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
    }

    public async Task<Result<ReleaseDto>> HandleAsync(
        CreateReleaseCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        var name = request.Name?.Trim() ?? string.Empty;
        var description = request.Description?.Trim();
        var branch = request.TargetBranch?.Trim() ?? string.Empty;
        var version = string.IsNullOrWhiteSpace(request.TargetVersion) ? null : request.TargetVersion.Trim();

        if (command.ProjectId == Guid.Empty || name.Length is < 1 or > 200)
            return Result.Failure<ReleaseDto>("Project id and a release name of 1-200 characters are required.", ErrorKind.Validation);
        if (description?.Length > 5000)
            return Result.Failure<ReleaseDto>("Release description cannot exceed 5000 characters.", ErrorKind.Validation);
        if (!ProjectVersionPolicyCatalog.IsSafeBranchName(branch))
            return Result.Failure<ReleaseDto>("Target branch is not a valid Git branch name.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var permission = await ReleaseAuthorization.RequireAdminAsync(
            db, _authorization, command.ProjectId, command.Actor, ct);
        if (!permission.Success)
            return Result.Forbidden<ReleaseDto>("Only a project or organization Owner/Admin can create a release.");

        var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, command.ProjectId, ct);
        if (!enabled.Success)
            return Result.Failure<ReleaseDto>(enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

        if (version is not null)
        {
            if (!ProjectVersionPolicyCatalog.TryNormalizeVersion(
                enabled.Value!, version, out var normalized, out var versionError))
                return Result.Failure<ReleaseDto>(
                    versionError ?? "Target version does not match the project version policy.", ErrorKind.Validation);
            version = normalized;
        }

        var release = Release.Create(name, description, command.ProjectId, version, branch, request.Order);
        db.Releases.Add(release);

        var orgId = await ReleaseAuthorization.GetOrgIdAsync(db, command.ProjectId, ct);
        _auditWriter.Stage(db, AuditEventFactory.ForActor(
            command.Actor, "project.release.created", "Release", release.Id.ToString("D"),
            orgId, command.ProjectId,
            detailsJson: JsonSerializer.Serialize(new
            {
                newState = release.Status.ToString(),
                targetVersion = release.TargetVersion ?? "Unspecified",
                targetBranch = release.TargetBranch,
                count = release.Order
            })));
        await db.SaveChangesAsync(ct);

        return Result.Ok(ReleaseMapping.ToDto(release, 0));
    }
}

public sealed class UpdateReleaseHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;

    public UpdateReleaseHandler(
        IBeaconDbFactory dbFactory, IAuthorizationService authorization, IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
    }

    public async Task<Result<ReleaseDto>> HandleAsync(
        UpdateReleaseCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (request.ReleaseId == Guid.Empty || request.ProjectId == Guid.Empty)
            return Result.Failure<ReleaseDto>("Release and project ids are required.", ErrorKind.Validation);
        var name = request.Name?.Trim();
        var description = request.Description?.Trim();
        var branch = request.TargetBranch?.Trim();
        var version = request.TargetVersion?.Trim();

        if (name is { Length: < 1 or > 200 } || description?.Length > 5000
            || (branch is not null && !ProjectVersionPolicyCatalog.IsSafeBranchName(branch)))
            return Result.Failure<ReleaseDto>("Release name, description, or target branch is invalid.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var permission = await ReleaseAuthorization.RequireAdminAsync(
            db, _authorization, request.ProjectId, command.Actor, ct);
        if (!permission.Success)
            return Result.Forbidden<ReleaseDto>("Only a project or organization Owner/Admin can update a release.");

        var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, request.ProjectId, ct);
        if (!enabled.Success)
            return Result.Failure<ReleaseDto>(enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

        var release = await db.Releases.FirstOrDefaultAsync(
            item => item.Id == request.ReleaseId && item.ProjectId == request.ProjectId, ct);
        if (release is null)
            return Result.Failure<ReleaseDto>("Release not found.", ErrorKind.NotFound);
        if (request.ExpectedRevision < 1 || request.ExpectedRevision != release.Revision)
            return Result.Failure<ReleaseDto>("Release changed since it was loaded. Refresh and retry.", ErrorKind.Conflict);

        if (!string.IsNullOrWhiteSpace(version))
        {
            if (!ProjectVersionPolicyCatalog.TryNormalizeVersion(
                enabled.Value!, version, out var normalized, out var versionError))
                return Result.Failure<ReleaseDto>(
                    versionError ?? "Target version does not match the project version policy.", ErrorKind.Validation);
            version = normalized;
        }

        var previousStatus = release.Status;
        try
        {
            release.Update(name, description, version, branch, request.Order);
            var orgId = await ReleaseAuthorization.GetOrgIdAsync(db, request.ProjectId, ct);
            _auditWriter.Stage(db, AuditEventFactory.ForActor(
                command.Actor, "project.release.updated", "Release", release.Id.ToString("D"),
                orgId, request.ProjectId,
                detailsJson: JsonSerializer.Serialize(new
                {
                    previousState = previousStatus.ToString(),
                    newState = "Release.FieldsUpdated",
                    targetVersion = release.TargetVersion ?? "Unspecified",
                    targetBranch = release.TargetBranch,
                    count = release.Revision
                })));
            await db.SaveChangesAsync(ct);
        }
        catch (InvalidOperationException exception)
        {
            return Result.Failure<ReleaseDto>(exception.Message, ErrorKind.Conflict);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ReleaseDto>("Release changed concurrently. Refresh and retry.", ErrorKind.Conflict);
        }

        return Result.Ok(ReleaseMapping.ToDto(
            release, await db.Milestones.CountAsync(milestone => milestone.ReleaseId == release.Id, ct)));
    }
}

public sealed class SetReleaseStatusHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;

    public SetReleaseStatusHandler(
        IBeaconDbFactory dbFactory, IAuthorizationService authorization, IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
    }

    public async Task<Result<ReleaseDto>> HandleAsync(
        SetReleaseStatusCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (!Enum.IsDefined(request.Status) || request.Status is ReleaseStatus.Candidate or ReleaseStatus.Published)
            return Result.Failure<ReleaseDto>(
                "Candidate and Published are reserved for the authorized ReleasePrepare/publication workflow.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var permission = await ReleaseAuthorization.RequireAdminAsync(
            db, _authorization, request.ProjectId, command.Actor, ct);
        if (!permission.Success)
            return Result.Forbidden<ReleaseDto>("Only a project or organization Owner/Admin can change release status.");
        var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, request.ProjectId, ct);
        if (!enabled.Success)
            return Result.Failure<ReleaseDto>(enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

        var release = await db.Releases.FirstOrDefaultAsync(
            item => item.Id == request.ReleaseId && item.ProjectId == request.ProjectId, ct);
        if (release is null)
            return Result.Failure<ReleaseDto>("Release not found.", ErrorKind.NotFound);
        if (request.ExpectedRevision < 1 || request.ExpectedRevision != release.Revision)
            return Result.Failure<ReleaseDto>("Release changed since it was loaded. Refresh and retry.", ErrorKind.Conflict);

        var previous = release.Status;
        try
        {
            release.TransitionTo(request.Status);
            var orgId = await ReleaseAuthorization.GetOrgIdAsync(db, request.ProjectId, ct);
            _auditWriter.Stage(db, AuditEventFactory.ForActor(
                command.Actor, "project.release.status_changed", "Release", release.Id.ToString("D"),
                orgId, request.ProjectId,
                detailsJson: JsonSerializer.Serialize(new
                {
                    previousState = previous.ToString(),
                    newState = release.Status.ToString()
                })));
            await db.SaveChangesAsync(ct);
        }
        catch (InvalidOperationException exception)
        {
            return Result.Failure<ReleaseDto>(exception.Message, ErrorKind.Conflict);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ReleaseDto>("Release changed concurrently. Refresh and retry.", ErrorKind.Conflict);
        }

        return Result.Ok(ReleaseMapping.ToDto(
            release, await db.Milestones.CountAsync(milestone => milestone.ReleaseId == release.Id, ct)));
    }
}

public sealed class DeleteReleaseHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;

    public DeleteReleaseHandler(
        IBeaconDbFactory dbFactory, IAuthorizationService authorization, IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(DeleteReleaseCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        await using var db = _dbFactory.CreateDbContext();
        var permission = await ReleaseAuthorization.RequireAdminAsync(
            db, _authorization, request.ProjectId, command.Actor, ct);
        if (!permission.Success)
            return Result.Forbidden("Only a project or organization Owner/Admin can delete a release.");
        var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, request.ProjectId, ct);
        if (!enabled.Success)
            return Result.Failure(enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

        var release = await db.Releases.FirstOrDefaultAsync(
            item => item.Id == request.ReleaseId && item.ProjectId == request.ProjectId, ct);
        if (release is null)
            return Result.Failure("Release not found.", ErrorKind.NotFound);
        if (release.Status is ReleaseStatus.Candidate or ReleaseStatus.Published
            || release.PreparationStatus != ReleasePreparationStatus.NotStarted)
            return Result.Failure("A Release with a prepared or in-flight Candidate cannot be deleted.", ErrorKind.Conflict);

        var linked = await db.Milestones
            .Where(milestone => milestone.ProjectId == request.ProjectId && milestone.ReleaseId == release.Id)
            .ToListAsync(ct);
        foreach (var milestone in linked)
            milestone.AssignRelease(null);

        var orgId = await ReleaseAuthorization.GetOrgIdAsync(db, request.ProjectId, ct);
        _auditWriter.Stage(db, AuditEventFactory.ForActor(
            command.Actor, "project.release.deleted", "Release", release.Id.ToString("D"),
            orgId, request.ProjectId,
            detailsJson: JsonSerializer.Serialize(new
            {
                previousState = release.Status.ToString(),
                newState = "Release.Deleted",
                targetVersion = release.TargetVersion ?? "Unspecified",
                targetBranch = release.TargetBranch,
                count = linked.Count
            })));
        db.Releases.Remove(release);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure("Release changed concurrently. Refresh and retry.", ErrorKind.Conflict);
        }
        return Result.Ok();
    }
}

public sealed class SetMilestoneReleaseHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;

    public SetMilestoneReleaseHandler(
        IBeaconDbFactory dbFactory, IAuthorizationService authorization, IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(SetMilestoneReleaseCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (request.ReleaseId == Guid.Empty)
            return Result.Failure("Release id cannot be empty.", ErrorKind.Validation);
        await using var db = _dbFactory.CreateDbContext();
        var permission = await ReleaseAuthorization.RequireAdminAsync(
            db, _authorization, request.ProjectId, command.Actor, ct);
        if (!permission.Success)
            return Result.Forbidden("Only a project or organization Owner/Admin can link a milestone to a release.");
        var enabled = await ReleaseAuthorization.RequireEnabledAsync(db, request.ProjectId, ct);
        if (!enabled.Success)
            return Result.Failure(enabled.Error ?? "Release Management is unavailable.", enabled.Kind);

        var milestone = await db.Milestones.FirstOrDefaultAsync(
            item => item.Id == request.MilestoneId && item.ProjectId == request.ProjectId, ct);
        if (milestone is null)
            return Result.Failure("Milestone not found.", ErrorKind.NotFound);
        if (request.ExpectedRevision < 1 || request.ExpectedRevision != milestone.Revision)
            return Result.Failure("Milestone changed since it was loaded. Refresh and retry.", ErrorKind.Conflict);

        if (milestone.ReleaseId is { } currentReleaseId)
        {
            var currentState = await db.Releases
                .Where(release => release.Id == currentReleaseId && release.ProjectId == request.ProjectId)
                .Select(release => new { release.Status, release.PreparationStatus })
                .FirstOrDefaultAsync(ct);
            if (currentState is not null
                && (currentState.Status is ReleaseStatus.Candidate or ReleaseStatus.Published
                    || currentState.PreparationStatus != ReleasePreparationStatus.NotStarted))
                return Result.Failure(
                    "Milestones cannot be detached or moved after ReleasePrepare has started.",
                    ErrorKind.Conflict);
        }

        if (request.ReleaseId is { } releaseId)
        {
            var exists = await db.Releases.AnyAsync(
                release => release.Id == releaseId
                    && release.ProjectId == request.ProjectId
                    && release.Status != ReleaseStatus.Candidate
                    && release.Status != ReleaseStatus.Published
                    && release.Status != ReleaseStatus.Cancelled
                    && release.PreparationStatus == ReleasePreparationStatus.NotStarted, ct);
            if (!exists)
                return Result.Failure("Release not found or no longer accepts milestones.", ErrorKind.NotFound);
        }

        var previousReleaseId = milestone.ReleaseId;
        milestone.AssignRelease(request.ReleaseId);
        var orgId = await ReleaseAuthorization.GetOrgIdAsync(db, request.ProjectId, ct);
        _auditWriter.Stage(db, AuditEventFactory.ForActor(
            command.Actor,
            "project.release.milestone_assigned",
            "Milestone",
            milestone.Id.ToString("D"),
            orgId,
            request.ProjectId,
            detailsJson: JsonSerializer.Serialize(new
            {
                previousReleaseId = previousReleaseId?.ToString("D") ?? "Unassigned",
                releaseId = milestone.ReleaseId?.ToString("D") ?? "Unassigned",
                count = milestone.Revision
            })));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure("Milestone changed concurrently. Refresh and retry.", ErrorKind.Conflict);
        }
        return Result.Ok();
    }
}
