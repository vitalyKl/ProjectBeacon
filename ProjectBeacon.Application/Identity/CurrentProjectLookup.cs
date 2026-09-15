namespace ProjectBeacon.Application.Identity;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public static class CurrentProjectLookup
{
    public static async Task<(Guid? ProjectId, Guid? OrgId)> ForUserAsync(
        BeaconDbContext db, Guid userId, bool isAdmin, Guid? preferredProjectId = null, CancellationToken ct = default)
    {
        if (preferredProjectId is { } preferred)
        {
            Guid? orgId = null;
            if (isAdmin)
            {
                orgId = await db.Projects.IgnoreQueryFilters()
                    .Where(p => p.Id == preferred)
                    .Select(p => (Guid?)p.OrgId)
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                orgId = await db.ProjectMembers.IgnoreQueryFilters()
                    .Where(m => m.UserId == userId && m.ProjectId == preferred)
                    .Select(m => (Guid?)m.Project.OrgId)
                    .FirstOrDefaultAsync(ct);
            }
            if (orgId is { } claimedOrg)
                return (preferred, claimedOrg);
        }

        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .Include(m => m.Project)
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.JoinedAt)
            .FirstOrDefaultAsync(ct);
        if (member is not null)
            return (member.ProjectId, member.Project.OrgId);

        if (!isAdmin)
            return (null, null);

        var project = await db.Projects.IgnoreQueryFilters()
            .OrderBy(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return (project?.Id, project?.OrgId);
    }
}
