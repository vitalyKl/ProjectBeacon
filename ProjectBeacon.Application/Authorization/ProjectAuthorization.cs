namespace ProjectBeacon.Application.Authorization;

using Application.Common;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public static class ProjectAuthorization
{
    public static async Task<bool> CanManageProjectAsync(
        BeaconDbContext db, Guid projectId, Guid actorUserId, bool isAdmin, bool isApiToken, CancellationToken ct)
    {
        if (isAdmin)
            return true;
        if (isApiToken)
            return false;
        var orgId = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is not null && await CanManageOrgAsync(db, orgId.Value, actorUserId, false, ct))
            return true;
        return await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actorUserId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
    }

    public static async Task<bool> CanManageOrgAsync(
        BeaconDbContext db, Guid orgId, Guid actorUserId, bool isAdmin, CancellationToken ct)
    {
        if (isAdmin)
            return true;
        return await db.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == orgId && m.UserId == actorUserId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
    }

    public static async Task<Result> AddMember(
        BeaconDbContext db, Guid projectId, Guid actorUserId, bool isAdmin, bool isApiToken,
        Guid targetUserId, MemberRole targetRole, CancellationToken ct)
    {
        if (isApiToken || !await CanManageProjectAsync(db, projectId, actorUserId, isAdmin, false, ct))
            return Result.Failure("Forbidden.");

        var actorRole = await GetActorRoleAsync(db, projectId, actorUserId, ct);
        if (!isAdmin && actorRole != MemberRole.Owner && targetRole == MemberRole.Owner)
            return Result.Failure("Only an Owner or system admin can grant Owner role.");

        return Result.Ok();
    }

    public static async Task<Result> RemoveMember(
        BeaconDbContext db, Guid projectId, Guid actorUserId, bool isAdmin, bool isApiToken,
        Guid targetUserId, CancellationToken ct)
    {
        if (isApiToken)
            return Result.Failure("Forbidden.");

        if (actorUserId == targetUserId)
            return Result.Ok();

        if (!await CanManageProjectAsync(db, projectId, actorUserId, isAdmin, false, ct))
            return Result.Failure("Forbidden.");

        var targetRole = await GetMemberRoleAsync(db, projectId, targetUserId, ct);
        if (targetRole == MemberRole.Owner)
        {
            var ownerCount = await db.ProjectMembers.IgnoreQueryFilters()
                .CountAsync(m => m.ProjectId == projectId && m.Role == MemberRole.Owner, ct);
            if (ownerCount <= 1)
                return Result.Failure("Cannot remove the last Owner.");
            if (!isAdmin)
            {
                var actorRole = await GetActorRoleAsync(db, projectId, actorUserId, ct);
                if (actorRole != MemberRole.Owner)
                    return Result.Failure("Only an Owner or system admin can remove an Owner.");
            }
        }

        return Result.Ok();
    }

    public static async Task<Result> ManageTokens(
        Guid actorUserId, bool isAdmin, bool isApiToken)
    {
        if (isApiToken || !isAdmin)
            return Result.Failure("Forbidden.");
        return Result.Ok();
    }

    public static async Task<Result> CreateToken(
        BeaconDbContext db, Guid projectId, Guid actorUserId, bool isAdmin, bool isApiToken,
        ApiTokenCapability requestedCapabilities, CancellationToken ct)
    {
        if (isApiToken || !await CanManageProjectAsync(db, projectId, actorUserId, isAdmin, false, ct))
            return Result.Failure("Forbidden.");

        if (requestedCapabilities.HasFlag(ApiTokenCapability.Admin) && !isAdmin)
        {
            var actorRole = await GetActorRoleAsync(db, projectId, actorUserId, ct);
            if (actorRole != MemberRole.Owner)
                return Result.Failure("Only an Owner or system admin can create Admin tokens.");
        }

        return Result.Ok();
    }

    public static async Task<Result> RevokeToken(
        BeaconDbContext db, Guid projectId, Guid actorUserId, bool isAdmin, bool isApiToken,
        Guid tokenId, CancellationToken ct)
    {
        if (isApiToken || !await CanManageProjectAsync(db, projectId, actorUserId, isAdmin, false, ct))
            return Result.Failure("Forbidden.");
        return Result.Ok();
    }

    private static async Task<MemberRole?> GetActorRoleAsync(
        BeaconDbContext db, Guid projectId, Guid actorUserId, CancellationToken ct)
    {
        var projectRole = await db.ProjectMembers.IgnoreQueryFilters()
            .Where(m => m.ProjectId == projectId && m.UserId == actorUserId)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(ct);
        if (projectRole != default || !await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actorUserId, ct))
            return projectRole;

        var orgId = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is null)
            return null;
        return await db.OrgMembers.IgnoreQueryFilters()
            .Where(m => m.OrgId == orgId.Value && m.UserId == actorUserId)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<MemberRole?> GetMemberRoleAsync(
        BeaconDbContext db, Guid projectId, Guid userId, CancellationToken ct)
    {
        return await db.ProjectMembers.IgnoreQueryFilters()
            .Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => (MemberRole?)m.Role)
            .FirstOrDefaultAsync(ct);
    }
}
