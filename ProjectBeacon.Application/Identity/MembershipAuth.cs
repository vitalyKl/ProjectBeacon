namespace ProjectBeacon.Application.Identity;

using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

internal static class MembershipAuth
{
    public static async Task<bool> CanManageOrgAsync(
        BeaconDbContext db, Guid orgId, Guid userId, bool isAdmin, CancellationToken ct)
    {
        if (isAdmin)
            return true;
        return await db.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == orgId && m.UserId == userId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
    }

    public static async Task<bool> CanManageProjectAsync(
        BeaconDbContext db, Guid projectId, Guid userId, bool isAdmin, CancellationToken ct)
    {
        if (isAdmin)
            return true;
        var orgId = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is null)
            return false;
        if (await CanManageOrgAsync(db, orgId.Value, userId, false, ct))
            return true;
        return await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == userId
                && (m.Role == MemberRole.Owner || m.Role == MemberRole.Admin), ct);
    }
}
