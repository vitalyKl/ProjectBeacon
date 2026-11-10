namespace ProjectBeacon.Application.Identity;

using Application.Persistence;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Picks the user's current project and org. An admin may use a preferred project id that ignores membership.
/// </summary>
public static class CurrentProjectLookup
{
    public static async Task<(Guid? ProjectId, Guid? OrgId)> ForUserAsync(
        IBeaconDb db, Guid userId, bool isAdmin, Guid? preferredProjectId = null, CancellationToken ct = default)
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
                    .Where(m => m.UserId == userId && m.ProjectId == preferred
                        && m.Project.Org.LifecycleStatus == OrganizationLifecycleStatus.Active)
                    .Select(m => (Guid?)m.Project.OrgId)
                    .FirstOrDefaultAsync(ct);
            }
            if (orgId is { } claimedOrg)
                return (preferred, claimedOrg);
        }

        // Prefer an active organization for a fresh login or a tenantless account. If the
        // user has only suspended/closed memberships, retain one as the selected tenant so
        // the session remains valid but lifecycle-aware tenant filters block its data.
        var activeMember = await db.ProjectMembers.IgnoreQueryFilters()
            .Include(m => m.Project)
            .Where(m => m.UserId == userId
                && m.Project.Org.LifecycleStatus == OrganizationLifecycleStatus.Active)
            .OrderBy(m => m.JoinedAt)
            .FirstOrDefaultAsync(ct);
        if (activeMember is not null)
            return (activeMember.ProjectId, activeMember.Project.OrgId);

        var existingMember = await db.ProjectMembers.IgnoreQueryFilters()
            .Include(m => m.Project)
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.JoinedAt)
            .FirstOrDefaultAsync(ct);
        if (existingMember is not null)
            return (existingMember.ProjectId, existingMember.Project.OrgId);

        if (!isAdmin)
            return (null, null);

        var activeProject = await db.Projects.IgnoreQueryFilters()
            .Where(project => project.Org.LifecycleStatus == OrganizationLifecycleStatus.Active)
            .OrderBy(project => project.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (activeProject is not null)
            return (activeProject.Id, activeProject.OrgId);

        var project = await db.Projects.IgnoreQueryFilters()
            .OrderBy(project => project.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return (project?.Id, project?.OrgId);
    }
}
