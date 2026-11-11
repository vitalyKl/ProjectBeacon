namespace ProjectBeacon.Application.Authorization;

using Application.Common;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Central resource authorization policy. Route filters are only the coarse actor/capability
/// gate; this service performs the resource scope and membership decision. Unknown combinations
/// fail closed.
/// </summary>
public sealed class AuthorizationService : IAuthorizationService
{
    private const ApiTokenCapability KnownCapabilities =
        ApiTokenCapability.TaskRead
        | ApiTokenCapability.TaskWrite
        | ApiTokenCapability.SessionDrive
        | ApiTokenCapability.ContextRead
        | ApiTokenCapability.Admin;

    public async Task<Result> CanAsync(
        IBeaconDb db,
        ActorContext actor,
        ResourceType resource,
        AuthAction action,
        Guid? projectId = null,
        Guid? orgId = null,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(resource) || !Enum.IsDefined(action)
            || !IsSupportedActionCombination(resource, action))
            return Result.Forbidden();

        if (actor.IsWorker)
            return Result.Forbidden();

        if (actor.IsDevice)
            return actor.DeviceId is { } deviceId && deviceId != Guid.Empty
                && (resource == ResourceType.Device && action is AuthAction.Read or AuthAction.Update
                    || resource == ResourceType.WorkstationCommand && action == AuthAction.Execute)
                    ? Result.Ok()
                    : Result.Forbidden();

        var effectiveProjectId = projectId;
        var effectiveOrgId = orgId;

        if (actor.IsApiToken)
        {
            if (actor.ProjectId is not { } boundProjectId || boundProjectId == Guid.Empty)
                return Result.Forbidden();

            if (effectiveProjectId is { } requestedProject && requestedProject != boundProjectId)
                return Result.Forbidden();

            effectiveProjectId = boundProjectId;
            var boundOrgId = await GetProjectOrgIdAsync(db, boundProjectId, ct);
            if (boundOrgId is null)
                return Result.Failure("Token project not found.", ErrorKind.NotFound);

            if (effectiveOrgId is { } requestedOrg && requestedOrg != boundOrgId)
                return Result.Forbidden();

            effectiveOrgId = boundOrgId;
        }

        // Customer access is always subordinate to organization lifecycle, including a
        // system-admin membership bypass and API-token capability.
        var lifecycle = await EnsureOrganizationActiveAsync(
            db, effectiveProjectId, effectiveOrgId, ct);
        if (!lifecycle.Success)
            return lifecycle;

        if (actor.IsHuman && actor.IsAdmin)
            return HasExplicitScope(resource, action, effectiveProjectId, effectiveOrgId, actor)
                ? Result.Ok()
                : Result.Forbidden();

        if (actor.IsApiToken)
            return TokenCanAccess(actor, resource, action, effectiveProjectId);

        if (actor.IsHuman)
            return await HumanCanAccessAsync(
                db, actor, resource, action, effectiveProjectId, effectiveOrgId, ct);

        return Result.Forbidden();
    }

    public async Task<Result> CanAddMemberAsync(
        IBeaconDb db,
        ActorContext actor,
        Guid projectId,
        Guid targetUserId,
        MemberRole targetRole,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(targetRole) || !actor.IsHuman)
            return Result.Forbidden();

        var manage = await CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer,
            projectId: projectId, ct: ct);
        if (!manage.Success)
            return manage;

        if (targetRole == MemberRole.Owner && !actor.IsAdmin)
        {
            var role = await GetActorRoleAsync(db, projectId, actor, ct);
            if (role != MemberRole.Owner)
                return Result.Failure("Only an Owner or system admin can grant Owner role.");
        }

        return Result.Ok();
    }

    public async Task<Result> CanRemoveMemberAsync(
        IBeaconDb db,
        ActorContext actor,
        Guid projectId,
        Guid targetUserId,
        CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden();

        var targetRole = await GetMemberRoleAsync(db, projectId, targetUserId, ct);
        if (targetRole is null)
            return Result.Forbidden();

        var isSelfRemoval = actor.UserId == targetUserId;
        if (isSelfRemoval)
        {
            // A member may leave without being a project administrator, but the project
            // must retain at least one Owner. Resolve lifecycle before exposing the outcome.
            var lifecycle = await EnsureOrganizationActiveAsync(db, projectId, null, ct);
            if (!lifecycle.Success)
                return lifecycle;

            if (targetRole == MemberRole.Owner)
            {
                var ownerCount = await db.ProjectMembers.IgnoreQueryFilters()
                    .CountAsync(m => m.ProjectId == projectId && m.Role == MemberRole.Owner, ct);
                if (ownerCount <= 1)
                    return Result.Failure(
                        "The last project Owner cannot leave or be removed.",
                        ErrorKind.Conflict);
            }

            return Result.Ok();
        }

        // Check the caller before revealing whether the target is the last Owner.
        // Otherwise an unrelated user could distinguish owner-count state via 409 vs 403.
        var manage = await CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer,
            projectId: projectId, ct: ct);
        if (!manage.Success)
            return Result.Forbidden();

        if (targetRole == MemberRole.Owner && !actor.IsAdmin)
        {
            var actorRole = await GetActorRoleAsync(db, projectId, actor, ct);
            if (actorRole != MemberRole.Owner)
                return Result.Forbidden();
        }

        if (targetRole == MemberRole.Owner)
        {
            var ownerCount = await db.ProjectMembers.IgnoreQueryFilters()
                .CountAsync(m => m.ProjectId == projectId && m.Role == MemberRole.Owner, ct);
            if (ownerCount <= 1)
                return Result.Failure(
                    "The last project Owner cannot leave or be removed.",
                    ErrorKind.Conflict);
        }

        return Result.Ok();
    }

    public async Task<Result> CanCreateTokenAsync(
        IBeaconDb db,
        ActorContext actor,
        Guid projectId,
        ApiTokenCapability requestedCapabilities,
        CancellationToken ct = default)
    {
        if (!actor.IsHuman
            || requestedCapabilities == ApiTokenCapability.None
            || (requestedCapabilities & ~KnownCapabilities) != 0)
            return Result.Forbidden();

        var manage = await CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer,
            projectId: projectId, ct: ct);
        if (!manage.Success)
            return manage;

        if (requestedCapabilities.HasFlag(ApiTokenCapability.Admin) && !actor.IsAdmin)
        {
            var role = await GetActorRoleAsync(db, projectId, actor, ct);
            if (role != MemberRole.Owner)
                return Result.Failure("Only an Owner or system admin can create Admin tokens.");
        }

        return Result.Ok();
    }

    /// <summary>Endpoint capability gate. Human/device identity is checked separately by actor metadata and CanAsync.</summary>
    public bool HasCapability(ActorContext actor, ApiTokenCapability required)
    {
        if (required == ApiTokenCapability.None || (required & ~KnownCapabilities) != 0)
            return false;
        if (!actor.IsApiToken)
            return actor.IsHuman || actor.IsDevice;
        return actor.Capabilities.HasFlag(ApiTokenCapability.Admin)
            || actor.Capabilities.HasFlag(required);
    }

    private async Task<Result> HumanCanAccessAsync(
        IBeaconDb db,
        ActorContext actor,
        ResourceType resource,
        AuthAction action,
        Guid? projectId,
        Guid? orgId,
        CancellationToken ct)
    {
        if (actor.UserId is not { } userId || userId == Guid.Empty)
            return Result.Forbidden();

        if (resource == ResourceType.Org && action == AuthAction.Create)
            return Result.Ok();

        if (IsOrganizationScoped(resource))
        {
            if (orgId is not { } scopedOrgId || scopedOrgId == Guid.Empty)
                return Result.Forbidden();

            if (resource == ResourceType.Org && action == AuthAction.Read)
                return await CanReadOrgAsync(db, userId, scopedOrgId, ct);

            return IsOrganizationManagerOnly(resource, action)
                ? await CanManageOrgAsync(db, actor, scopedOrgId, ct)
                : Result.Forbidden();
        }

        if (!IsProjectScoped(resource)
            || projectId is not { } scopedProjectId
            || scopedProjectId == Guid.Empty)
            return Result.Forbidden();

        if (IsProjectManagerOnly(resource, action))
            return await CanManageProjectAsync(db, actor, scopedProjectId, ct);

        // Membership is not a blanket allow. A known resource/action pair must also
        // appear in the normal-member policy. Resource-owner/participant actions that
        // need an entity id are deliberately excluded until their dedicated handler
        // check supplies that context.
        if (!IsProjectMemberActionAllowed(resource, action))
            return Result.Forbidden();

        return await CanUseProjectAsync(db, actor, scopedProjectId, ct);
    }

    private static bool HasExplicitScope(
        ResourceType resource,
        AuthAction action,
        Guid? projectId,
        Guid? orgId,
        ActorContext actor)
    {
        if (resource == ResourceType.Org && action == AuthAction.Create)
            return actor.UserId is not null;
        if (IsOrganizationScoped(resource))
            return orgId is { } scopedOrgId && scopedOrgId != Guid.Empty;
        if (IsProjectScoped(resource))
            return projectId is { } scopedProjectId && scopedProjectId != Guid.Empty;
        return false;
    }

    private static bool IsSupportedActionCombination(ResourceType resource, AuthAction action)
    {
        if (action == AuthAction.Administer)
            return IsOrganizationScoped(resource) || IsProjectScoped(resource);

        return resource switch
        {
            ResourceType.Project => action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Delete,
            ResourceType.Org => action is AuthAction.Read or AuthAction.Create or AuthAction.Update,
            ResourceType.ProjectMember or ResourceType.OrgMember =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Delete,
            ResourceType.ApiToken => action is AuthAction.Read or AuthAction.Create or AuthAction.Delete,
            ResourceType.ProjectInvite or ResourceType.OrgInvite =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Delete or AuthAction.Execute,
            ResourceType.Task or ResourceType.TaskStep =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Delete,
            ResourceType.Pipeline =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Execute,
            ResourceType.Context or ResourceType.Constraint or ResourceType.Decision or ResourceType.Milestone =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Delete or AuthAction.Execute,
            ResourceType.Label => action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Delete,
            ResourceType.Report => action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Delete,
            ResourceType.ChatSession =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Delete or AuthAction.Execute,
            ResourceType.Device =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Delete or AuthAction.Execute,
            ResourceType.WorkstationCommand => action == AuthAction.Execute,
            ResourceType.ProjectRuntime or ResourceType.ModelBackend
                or ResourceType.AgentTemplate or ResourceType.McpConfiguration
                or ResourceType.WorkstationConfiguration =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Delete or AuthAction.Execute,
            ResourceType.EvalRun =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Delete or AuthAction.Execute,
            ResourceType.Work => action == AuthAction.Execute,
            _ => false
        };
    }

    private static bool IsProjectMemberActionAllowed(ResourceType resource, AuthAction action) =>
        resource switch
        {
            ResourceType.Project => action == AuthAction.Read,
            ResourceType.Task => action is AuthAction.Read or AuthAction.Create or AuthAction.Update,
            ResourceType.TaskStep => action is AuthAction.Read or AuthAction.Create,
            ResourceType.Pipeline =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Execute,
            ResourceType.Context =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update or AuthAction.Execute,
            ResourceType.Constraint =>
                action is AuthAction.Read or AuthAction.Create or AuthAction.Update,
            ResourceType.Decision => action is AuthAction.Read or AuthAction.Create,
            ResourceType.Milestone => action == AuthAction.Read,
            ResourceType.Label => action == AuthAction.Read,
            ResourceType.Report => action is AuthAction.Read or AuthAction.Create,
            ResourceType.ProjectRuntime or ResourceType.ModelBackend
                or ResourceType.AgentTemplate or ResourceType.McpConfiguration =>
                action is AuthAction.Read or AuthAction.Execute,
            ResourceType.EvalRun => action is AuthAction.Read or AuthAction.Create or AuthAction.Execute,
            ResourceType.Work => action == AuthAction.Execute,
            _ => false
        };

    private static bool IsOrganizationScoped(ResourceType resource) =>
        resource is ResourceType.Org or ResourceType.OrgMember or ResourceType.OrgInvite;

    private static bool IsProjectScoped(ResourceType resource) =>
        resource is ResourceType.Project or ResourceType.ProjectMember
            or ResourceType.ApiToken or ResourceType.ProjectInvite
            or ResourceType.Task or ResourceType.TaskStep or ResourceType.Pipeline
            or ResourceType.Context or ResourceType.Constraint or ResourceType.Decision
            or ResourceType.Milestone or ResourceType.Label or ResourceType.Report
            or ResourceType.ProjectRuntime or ResourceType.ModelBackend
            or ResourceType.AgentTemplate or ResourceType.McpConfiguration
            or ResourceType.WorkstationConfiguration or ResourceType.EvalRun or ResourceType.Work
            or ResourceType.Device;

    private static bool IsOrganizationManagerOnly(ResourceType resource, AuthAction action) =>
        resource is ResourceType.OrgMember or ResourceType.OrgInvite
        || resource == ResourceType.Org && action is not AuthAction.Read;

    private static bool IsProjectManagerOnly(ResourceType resource, AuthAction action)
    {
        if (action == AuthAction.Administer)
            return true;

        return resource switch
        {
            ResourceType.Project => action is AuthAction.Create or AuthAction.Update or AuthAction.Delete,
            ResourceType.ProjectMember or ResourceType.ApiToken or ResourceType.ProjectInvite => true,
            ResourceType.Task => action == AuthAction.Delete,
            ResourceType.TaskStep => action is AuthAction.Update or AuthAction.Delete,
            ResourceType.Context => action == AuthAction.Delete,
            ResourceType.Constraint => action is AuthAction.Delete or AuthAction.Execute,
            ResourceType.Decision => action is AuthAction.Update or AuthAction.Delete or AuthAction.Execute,
            ResourceType.Milestone => action is AuthAction.Create or AuthAction.Update or AuthAction.Delete or AuthAction.Execute,
            ResourceType.Label => action is AuthAction.Create or AuthAction.Update or AuthAction.Delete,
            ResourceType.Report => action is AuthAction.Update or AuthAction.Delete,
            ResourceType.ProjectRuntime => action is AuthAction.Create or AuthAction.Update or AuthAction.Delete,
            ResourceType.ModelBackend => action is AuthAction.Create or AuthAction.Update or AuthAction.Delete,
            ResourceType.AgentTemplate or ResourceType.McpConfiguration or ResourceType.WorkstationConfiguration
                => action is AuthAction.Create or AuthAction.Update or AuthAction.Delete,
            ResourceType.EvalRun => action is AuthAction.Update or AuthAction.Delete,
            ResourceType.Device => true,
            _ => false
        };
    }

    private async Task<Result> CanUseProjectAsync(
        IBeaconDb db,
        ActorContext actor,
        Guid projectId,
        CancellationToken ct)
    {
        if (actor.UserId is not { } userId || userId == Guid.Empty)
            return Result.Forbidden();

        var projectOrgId = await GetProjectOrgIdAsync(db, projectId, ct);
        if (projectOrgId is null)
            return Result.Failure("Project not found.", ErrorKind.NotFound);

        var isProjectMember = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (isProjectMember)
            return Result.Ok();

        // Org Owners/Admins inherit project administration within their organization,
        // but an Org Member does not implicitly become a project Member.
        return await CanManageOrgAsync(db, actor, projectOrgId.Value, ct);
    }

    private static async Task<Result> CanReadOrgAsync(
        IBeaconDb db,
        Guid userId,
        Guid orgId,
        CancellationToken ct)
    {
        var isMember = await db.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == orgId && m.UserId == userId, ct);
        return isMember ? Result.Ok() : Result.Forbidden();
    }

    private static async Task<Result> EnsureOrganizationActiveAsync(
        IBeaconDb db,
        Guid? projectId,
        Guid? orgId,
        CancellationToken ct)
    {
        var targetOrgId = orgId;
        if (targetOrgId is null && projectId is { } requestedProject)
        {
            targetOrgId = await db.Projects.IgnoreQueryFilters()
                .Where(project => project.Id == requestedProject)
                .Select(project => (Guid?)project.OrgId)
                .FirstOrDefaultAsync(ct);
            if (targetOrgId is null)
                return Result.Failure("Project not found.", ErrorKind.NotFound);
        }

        if (targetOrgId is not { } resolvedOrgId)
            return Result.Ok();

        var status = await db.Orgs.IgnoreQueryFilters()
            .Where(org => org.Id == resolvedOrgId)
            .Select(org => (OrganizationLifecycleStatus?)org.LifecycleStatus)
            .FirstOrDefaultAsync(ct);
        if (status is null)
            return Result.Failure("Organization not found.", ErrorKind.NotFound);
        return status == OrganizationLifecycleStatus.Active
            ? Result.Ok()
            : Result.Failure(
                status == OrganizationLifecycleStatus.Suspended
                    ? "Organization access is suspended."
                    : "Organization is closed.",
                ErrorKind.Forbidden);
    }

    private async Task<Result> CanManageProjectAsync(
        IBeaconDb db,
        ActorContext actor,
        Guid projectId,
        CancellationToken ct)
    {
        var orgId = await GetProjectOrgIdAsync(db, projectId, ct);
        if (orgId is null)
            return Result.Failure("Project not found.", ErrorKind.NotFound);

        var orgCheck = await CanManageOrgAsync(db, actor, orgId.Value, ct);
        if (orgCheck.Success)
            return Result.Ok();

        var isProjectAdmin = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actor.UserId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
        return isProjectAdmin ? Result.Ok() : Result.Forbidden();
    }

    private static async Task<Result> CanManageOrgAsync(
        IBeaconDb db,
        ActorContext actor,
        Guid orgId,
        CancellationToken ct)
    {
        if (actor.UserId is not { } userId || userId == Guid.Empty)
            return Result.Forbidden();

        var isOrgAdmin = await db.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == orgId && m.UserId == userId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
        return isOrgAdmin ? Result.Ok() : Result.Forbidden();
    }

    private static async Task<Guid?> GetProjectOrgIdAsync(
        IBeaconDb db,
        Guid projectId,
        CancellationToken ct) =>
        await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);

    private static Result TokenCanAccess(
        ActorContext actor,
        ResourceType resource,
        AuthAction action,
        Guid? projectId)
    {
        if (actor.ProjectId is not { } boundProject || boundProject == Guid.Empty
            || projectId != boundProject)
            return Result.Forbidden();

        return resource switch
        {
            ResourceType.Task when action is AuthAction.Read
                => TokenCan(actor, ApiTokenCapability.TaskRead),
            ResourceType.Task when action is AuthAction.Create or AuthAction.Update
                => TokenCan(actor, ApiTokenCapability.TaskWrite),
            ResourceType.TaskStep when action is AuthAction.Read
                => TokenCan(actor, ApiTokenCapability.TaskRead),
            ResourceType.TaskStep when action is AuthAction.Create
                => TokenCan(actor, ApiTokenCapability.TaskWrite),
            ResourceType.Work when action is AuthAction.Execute
                => TokenCan(actor, ApiTokenCapability.TaskWrite),
            ResourceType.Pipeline when action is AuthAction.Read
                => TokenCan(actor, ApiTokenCapability.TaskRead),
            ResourceType.Pipeline when action is AuthAction.Create or AuthAction.Update or AuthAction.Execute
                => TokenCan(actor, ApiTokenCapability.SessionDrive),
            ResourceType.Context or ResourceType.Decision when action is AuthAction.Read
                => TokenCan(actor, ApiTokenCapability.ContextRead),
            ResourceType.Label when action is AuthAction.Read
                => TokenCan(actor, ApiTokenCapability.TaskRead),
            _ => Result.Forbidden()
        };
    }

    private static Result TokenCan(ActorContext actor, ApiTokenCapability required) =>
        actor.Capabilities.HasFlag(ApiTokenCapability.Admin)
            || actor.Capabilities.HasFlag(required)
                ? Result.Ok()
                : Result.Forbidden();

    private static async Task<MemberRole?> GetActorRoleAsync(
        IBeaconDb db,
        Guid projectId,
        ActorContext actor,
        CancellationToken ct)
    {
        if (actor.UserId is not { } userId)
            return null;

        var projectRole = await db.ProjectMembers.IgnoreQueryFilters()
            .Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => (MemberRole?)m.Role)
            .FirstOrDefaultAsync(ct);
        if (projectRole is not null)
            return projectRole;

        var orgId = await GetProjectOrgIdAsync(db, projectId, ct);
        if (orgId is null)
            return null;
        return await db.OrgMembers.IgnoreQueryFilters()
            .Where(m => m.OrgId == orgId.Value && m.UserId == userId)
            .Select(m => (MemberRole?)m.Role)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<MemberRole?> GetMemberRoleAsync(
        IBeaconDb db,
        Guid projectId,
        Guid userId,
        CancellationToken ct) =>
        await db.ProjectMembers.IgnoreQueryFilters()
            .Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => (MemberRole?)m.Role)
            .FirstOrDefaultAsync(ct);
}
