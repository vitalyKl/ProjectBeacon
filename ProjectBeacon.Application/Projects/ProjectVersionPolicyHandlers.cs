namespace ProjectBeacon.Application.Projects;

using System.Text.Json;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Projects;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Version policy read model; OfficialVersion is authoritative state, not a repository projection.</summary>
public sealed record ProjectVersionPolicyDto(
    string Scheme,
    int ComponentCount,
    string? CalendarFormat,
    string TagPattern,
    bool AllowPrerelease,
    IReadOnlyList<ProjectVersionSourcePolicy> Sources,
    bool ReleaseManagementEnabled,
    string? DeploymentVersionEndpoint,
    string? OfficialVersion,
    int Revision);

public sealed record SetProjectVersionPolicyCommand(
    Guid ProjectId,
    ProjectVersionPolicy Policy,
    string? InitialOfficialVersion,
    int ExpectedRevision,
    ActorContext Actor);

public sealed class GetProjectVersionPolicyHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public GetProjectVersionPolicyHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<ProjectVersionPolicyDto>> HandleAsync(
        Guid projectId, ActorContext actor, CancellationToken ct = default)
    {
        if (projectId == Guid.Empty)
            return Result.Failure<ProjectVersionPolicyDto>("Project id is required.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        if (actor.IsApiToken)
        {
            if (actor.ProjectId != projectId
                || !actor.Capabilities.HasFlag(ApiTokenCapability.TaskRead))
                return Result.Forbidden<ProjectVersionPolicyDto>();
        }
        else if (actor.IsHuman && !actor.IsAdmin)
        {
            var projectMember = await db.ProjectMembers.AnyAsync(
                item => item.ProjectId == projectId && item.UserId == actor.UserId, ct);
            if (!projectMember)
            {
                var orgId = await db.Projects
                    .Where(project => project.Id == projectId)
                    .Select(project => (Guid?)project.OrgId)
                    .FirstOrDefaultAsync(ct);
                var orgMember = orgId is not null && await db.OrgMembers.AnyAsync(
                    item => item.OrgId == orgId && item.UserId == actor.UserId, ct);
                if (!orgMember)
                    return Result.Forbidden<ProjectVersionPolicyDto>();
            }
        }
        else if (!actor.IsHuman)
        {
            return Result.Forbidden<ProjectVersionPolicyDto>();
        }

        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == projectId, ct);
        if (project is null)
            return Result.Failure<ProjectVersionPolicyDto>("Project not found.", ErrorKind.NotFound);
        if (!ProjectVersionPolicyCatalog.TryRead(project.VersionPolicyJson, out var policy, out var error))
            return Result.Failure<ProjectVersionPolicyDto>(
                error ?? "Stored version policy is invalid.", ErrorKind.Conflict);
        return Result.Ok(Map(policy, project.OfficialVersion, project.VersionPolicyRevision));
    }

    internal static ProjectVersionPolicyDto Map(
        ProjectVersionPolicy policy, string? officialVersion, int revision) =>
        new(policy.Scheme, policy.ComponentCount, policy.CalendarFormat, policy.TagPattern,
            policy.AllowPrerelease, policy.Sources, policy.ReleaseManagementEnabled,
            policy.DeploymentVersionEndpoint, officialVersion, revision);
}

public sealed class SetProjectVersionPolicyHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;

    public SetProjectVersionPolicyHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
    }

    public async Task<Result<ProjectVersionPolicyDto>> HandleAsync(
        SetProjectVersionPolicyCommand command, CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<ProjectVersionPolicyDto>(
                "Only a human project Owner/Admin can change the version policy.");
        if (!ProjectVersionPolicyCatalog.Validate(command.Policy, out var validationError))
            return Result.Failure<ProjectVersionPolicyDto>(
                validationError ?? "Version policy is invalid.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Project, AuthAction.Administer,
            projectId: command.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Forbidden<ProjectVersionPolicyDto>(
                "Only a project or organization Owner/Admin can change the version policy.");

        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == command.ProjectId, ct);
        if (project is null)
            return Result.Failure<ProjectVersionPolicyDto>("Project not found.", ErrorKind.NotFound);
        if (command.ExpectedRevision < 1 || command.ExpectedRevision != project.VersionPolicyRevision)
            return Result.Failure<ProjectVersionPolicyDto>(
                "Version policy changed since it was loaded. Refresh settings and retry.", ErrorKind.Conflict);

        if (project.VersionPolicyJson is not null
            && ProjectVersionPolicyCatalog.TryRead(project.VersionPolicyJson, out var previousPolicy, out _)
            && previousPolicy.ReleaseManagementEnabled
            && !command.Policy.ReleaseManagementEnabled)
        {
            var activeReleases = await db.Releases.AnyAsync(
                release => release.ProjectId == command.ProjectId
                    && release.Status != ReleaseStatus.Cancelled
                    && release.Status != ReleaseStatus.Published, ct);
            if (activeReleases)
                return Result.Failure<ProjectVersionPolicyDto>(
                    "Release Management cannot be disabled while Planned, InProgress, or Candidate releases exist. Cancel or publish them first.",
                    ErrorKind.Conflict);
        }

        if (project.OfficialVersion is not null
            && !ProjectVersionPolicyCatalog.TryNormalizeVersion(
                command.Policy, project.OfficialVersion, out _, out var currentVersionError))
            return Result.Failure<ProjectVersionPolicyDto>(
                "The new policy would invalidate the authoritative official version: " + currentVersionError,
                ErrorKind.Validation);

        string? initialVersion = null;
        if (!string.IsNullOrWhiteSpace(command.InitialOfficialVersion))
        {
            if (project.OfficialVersion is not null
                && !string.Equals(project.OfficialVersion, command.InitialOfficialVersion.Trim(), StringComparison.Ordinal))
                return Result.Failure<ProjectVersionPolicyDto>(
                    "The official version is immutable outside ReleasePrepare. Version policy settings may initialize it once only.",
                    ErrorKind.Conflict);
            if (!ProjectVersionPolicyCatalog.TryNormalizeVersion(
                command.Policy, command.InitialOfficialVersion, out initialVersion, out validationError))
                return Result.Failure<ProjectVersionPolicyDto>(
                    validationError ?? "Initial official version is invalid.", ErrorKind.Validation);
        }

        var policyJson = ProjectVersionPolicyCatalog.Serialize(command.Policy);
        var currentJson = project.VersionPolicyJson;
        var currentState = project.OfficialVersion;
        var policyOrInitialVersionChanges =
            !string.Equals(currentJson, policyJson, StringComparison.Ordinal)
            || (currentState is null && initialVersion is not null);
        if (policyOrInitialVersionChanges
            && await db.Releases.AnyAsync(release => release.ProjectId == command.ProjectId
                && release.Status != ReleaseStatus.Cancelled
                && release.Status != ReleaseStatus.Published
                && release.PreparationStatus != ReleasePreparationStatus.NotStarted, ct))
            return Result.Failure<ProjectVersionPolicyDto>(
                "Version policy and initial official version are locked while a ReleasePrepare workflow is active. Cancel a failed Release before changing policy.",
                ErrorKind.Conflict);

        if (string.Equals(currentJson, policyJson, StringComparison.Ordinal)
            && (initialVersion is null || currentState is not null))
            return Result.Ok(GetProjectVersionPolicyHandler.Map(
                command.Policy, project.OfficialVersion, project.VersionPolicyRevision));

        project.SetVersionPolicyJson(policyJson);
        if (project.OfficialVersion is null && initialVersion is not null)
            project.InitializeOfficialVersion(initialVersion);

        _auditWriter.Stage(db, AuditEventFactory.ForActor(
            command.Actor,
            "project.version_policy.updated",
            "ProjectVersionPolicy",
            project.Id.ToString("D"),
            project.OrgId,
            project.Id,
            detailsJson: JsonSerializer.Serialize(new
            {
                newState = command.Policy.Scheme,
                count = command.Policy.Sources.Count,
                policyCode = command.Policy.ReleaseManagementEnabled
                    ? "ReleaseManagement.Enabled"
                    : "ReleaseManagement.Disabled"
            })));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ProjectVersionPolicyDto>(
                "Version policy changed concurrently. Refresh settings and retry.", ErrorKind.Conflict);
        }

        return Result.Ok(GetProjectVersionPolicyHandler.Map(
            command.Policy, project.OfficialVersion, project.VersionPolicyRevision));
    }
}
