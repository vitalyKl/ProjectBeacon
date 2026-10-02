namespace ProjectBeacon.Application.Authorization;

using Application.Common;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed class AuthorizationService : IAuthorizationService
{
    public async Task<Result> CanAsync(
        IBeaconDb db, ActorContext actor,
        ResourceType resource, AuthAction action,
        Guid? projectId = null, Guid? orgId = null,
        CancellationToken ct = default)
    {
        if (actor.IsWorker)
            return Result.Forbidden();

        if (actor.IsDevice)
            return resource is ResourceType.Device or ResourceType.WorkstationCommand
                ? Result.Ok()
                : Result.Forbidden();

        if (actor.IsAdmin)
            return Result.Ok();

        if (actor.IsApiToken)
            return TokenCanAccess(actor, resource, action);

        return await HumanCanAccessAsync(db, actor, resource, action, projectId, orgId, ct);
    }

    public async Task<Result> CanAddMemberAsync(
        IBeaconDb db, ActorContext actor, Guid projectId,
        Guid targetUserId, MemberRole targetRole,
        CancellationToken ct = default)
    {
        if (actor.IsApiToken)
            return Result.Forbidden();

        var manage = await CanAsync(db, actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
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
        IBeaconDb db, ActorContext actor, Guid projectId,
        Guid targetUserId,
        CancellationToken ct = default)
    {
        if (actor.IsApiToken)
            return Result.Forbidden();

        if (actor.UserId == targetUserId)
            return Result.Ok();

        var manage = await CanAsync(db, actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
        if (!manage.Success)
            return manage;

        var targetRole = await GetMemberRoleAsync(db, projectId, targetUserId, ct);
        if (targetRole == MemberRole.Owner)
        {
            var ownerCount = await db.ProjectMembers.IgnoreQueryFilters()
                .CountAsync(m => m.ProjectId == projectId && m.Role == MemberRole.Owner, ct);
            if (ownerCount <= 1)
                return Result.Failure("Cannot remove the last Owner.");
            if (!actor.IsAdmin)
            {
                var actorRole = await GetActorRoleAsync(db, projectId, actor, ct);
                if (actorRole != MemberRole.Owner)
                    return Result.Failure("Only an Owner or system admin can remove an Owner.");
            }
        }

        return Result.Ok();
    }

    public async Task<Result> CanCreateTokenAsync(
        IBeaconDb db, ActorContext actor, Guid projectId,
        ApiTokenCapability requestedCapabilities,
        CancellationToken ct = default)
    {
        if (actor.IsApiToken)
            return Result.Forbidden();

        var manage = await CanAsync(db, actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
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

    public bool HasCapability(ActorContext actor, ApiTokenCapability required)
    {
        if (!actor.IsApiToken)
            return true;
        return actor.Capabilities.HasFlag(required);
    }

    private async Task<Result> HumanCanAccessAsync(
        IBeaconDb db, ActorContext actor,
        ResourceType resource, AuthAction action,
        Guid? projectId, Guid? orgId,
        CancellationToken ct)
    {
        if (resource == ResourceType.Project && action == AuthAction.Administer)
            return await CanManageProjectAsync(db, actor, projectId!.Value, ct);
        if (resource == ResourceType.Org && action == AuthAction.Administer)
            return await CanManageOrgAsync(db, actor, orgId!.Value, ct);

        return Result.Ok();
    }

    private async Task<Result> CanManageProjectAsync(
        IBeaconDb db, ActorContext actor, Guid projectId, CancellationToken ct)
    {
        var orgId = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is not null)
        {
            var orgCheck = await CanManageOrgAsync(db, actor, orgId.Value, ct);
            if (orgCheck.Success)
                return Result.Ok();
        }

        var isProjectAdmin = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actor.UserId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
        return isProjectAdmin ? Result.Ok() : Result.Forbidden();
    }

    private async Task<Result> CanManageOrgAsync(
        IBeaconDb db, ActorContext actor, Guid orgId, CancellationToken ct)
    {
        var isOrgAdmin = await db.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == orgId && m.UserId == actor.UserId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
        return isOrgAdmin ? Result.Ok() : Result.Forbidden();
    }

    private static Result TokenCanAccess(ActorContext actor, ResourceType resource, AuthAction action)
    {
        return resource switch
        {
            ResourceType.Task or ResourceType.TaskStep
                when action is AuthAction.Read
                    => TokenCan(actor, ApiTokenCapability.TaskRead),
            ResourceType.Task or ResourceType.TaskStep
                when action is AuthAction.Create or AuthAction.Update or AuthAction.Delete
                    => TokenCan(actor, ApiTokenCapability.TaskWrite),
            ResourceType.Context
                when action is AuthAction.Read
                    => TokenCan(actor, ApiTokenCapability.ContextRead),
            ResourceType.Pipeline or ResourceType.ChatSession
                when action is AuthAction.Read or AuthAction.Execute
                    => TokenCan(actor, ApiTokenCapability.SessionDrive),
            _ => Result.Forbidden(),
        };
    }

    private static Result TokenCan(ActorContext actor, ApiTokenCapability required)
        => actor.Capabilities.HasFlag(required) ? Result.Ok() : Result.Forbidden();

    private static async Task<MemberRole?> GetActorRoleAsync(
        IBeaconDb db, Guid projectId, ActorContext actor, CancellationToken ct)
    {
        var userId = actor.UserId;
        var projectRole = await db.ProjectMembers.IgnoreQueryFilters()
            .Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(ct);
        if (projectRole != default || !await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct))
            return projectRole;

        var orgId = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is null)
            return null;
        return await db.OrgMembers.IgnoreQueryFilters()
            .Where(m => m.OrgId == orgId.Value && m.UserId == userId)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<MemberRole?> GetMemberRoleAsync(
        IBeaconDb db, Guid projectId, Guid userId, CancellationToken ct)
    {
        return await db.ProjectMembers.IgnoreQueryFilters()
            .Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => (MemberRole?)m.Role)
            .FirstOrDefaultAsync(ct);
    }
}
