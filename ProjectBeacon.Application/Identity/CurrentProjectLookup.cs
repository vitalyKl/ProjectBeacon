namespace ProjectBeacon.Application.Identity;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public static class CurrentProjectLookup
{
    public static async Task<(Guid? ProjectId, Guid? OrgId)> ForUserAsync(
        BeaconDbContext db, Guid userId, bool isAdmin, CancellationToken ct = default)
    {
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
