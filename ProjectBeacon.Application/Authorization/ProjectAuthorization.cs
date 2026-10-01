namespace ProjectBeacon.Application.Authorization;

using Application.Common;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public static class ProjectAuthorization
{
    public static async Task<bool> CanManageProjectAsync(
        IBeaconDb db, Guid projectId, ActorContext actor, CancellationToken ct)
    {
        if (actor.IsAdmin)
            return true;
        if (actor.IsApiToken)
            return false;
        var orgId = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is not null && await CanManageOrgAsync(db, orgId.Value, actor, ct))
            return true;
        return await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actor.UserId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
    }

    public static async Task<bool> CanManageOrgAsync(
        IBeaconDb db, Guid orgId, ActorContext actor, CancellationToken ct)
    {
        if (actor.IsAdmin)
            return true;
        return await db.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == orgId && m.UserId == actor.UserId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
    }

    public static async Task<Result> AddMember(
        IBeaconDb db, Guid projectId, ActorContext actor,
        Guid targetUserId, MemberRole targetRole, CancellationToken ct)
    {
        if (actor.IsApiToken || !await CanManageProjectAsync(db, projectId, actor, ct))
            return Result.Forbidden();

        var actorRole = await GetActorRoleAsync(db, projectId, actor, ct);
        if (!actor.IsAdmin && actorRole != MemberRole.Owner && targetRole == MemberRole.Owner)
            return Result.Failure("Only an Owner or system admin can grant Owner role.");

        return Result.Ok();
    }

    public static async Task<Result> RemoveMember(
        IBeaconDb db, Guid projectId, ActorContext actor,
        Guid targetUserId, CancellationToken ct)
    {
        if (actor.IsApiToken)
            return Result.Forbidden();

        if (actor.UserId == targetUserId)
            return Result.Ok();

        if (!await CanManageProjectAsync(db, projectId, actor, ct))
            return Result.Forbidden();

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

    public static async Task<Result> ManageTokens(ActorContext actor)
    {
        if (actor.IsApiToken || !actor.IsAdmin)
            return Result.Forbidden();
        return Result.Ok();
    }

    public static async Task<Result> CreateToken(
        IBeaconDb db, Guid projectId, ActorContext actor,
        ApiTokenCapability requestedCapabilities, CancellationToken ct)
    {
        if (actor.IsApiToken || !await CanManageProjectAsync(db, projectId, actor, ct))
            return Result.Forbidden();

        if (requestedCapabilities.HasFlag(ApiTokenCapability.Admin) && !actor.IsAdmin)
        {
            var actorRole = await GetActorRoleAsync(db, projectId, actor, ct);
            if (actorRole != MemberRole.Owner)
                return Result.Failure("Only an Owner or system admin can create Admin tokens.");
        }

        return Result.Ok();
    }

    public static async Task<Result> RevokeToken(
        IBeaconDb db, Guid projectId, ActorContext actor,
        Guid tokenId, CancellationToken ct)
    {
        if (actor.IsApiToken || !await CanManageProjectAsync(db, projectId, actor, ct))
            return Result.Forbidden();
        return Result.Ok();
    }

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
