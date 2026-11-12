namespace ProjectBeacon.Application.Identity;

using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// Picks the user's current project and org. An admin may use a preferred project id that ignores membership.
/// Organization Owner/Admin roles also inherit project scope within their organizations.
/// </summary>
public static class CurrentProjectLookup
{
    public static async Task<(Guid? ProjectId, Guid? OrgId)> ForUserAsync(
        IBeaconDb db, Guid userId, bool isAdmin, Guid? preferredProjectId = null,
        CancellationToken ct = default, Guid? preferredOrgId = null)
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

                orgId ??= await GetManagedProjectOrgIdAsync(db, userId, preferred, ct);
            }

            if (orgId is { } claimedOrg
                && (preferredOrgId is null || preferredOrgId == claimedOrg))
                return (preferred, claimedOrg);
        }

        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .Include(m => m.Project)
            .Where(m => m.UserId == userId
                && (preferredOrgId == null || m.Project.OrgId == preferredOrgId))
            .OrderBy(m => m.JoinedAt)
            .FirstOrDefaultAsync(ct);
        if (member is not null)
            return (member.ProjectId, member.Project.OrgId);

        if (!isAdmin)
        {
            var managed = await db.Projects.IgnoreQueryFilters()
                .Where(project => (preferredOrgId == null || project.OrgId == preferredOrgId)
                    && db.OrgMembers.IgnoreQueryFilters().Any(orgMember =>
                    orgMember.OrgId == project.OrgId
                    && orgMember.UserId == userId
                    && (orgMember.Role == MemberRole.Owner || orgMember.Role == MemberRole.Admin)))
                .OrderBy(project => project.CreatedAt)
                .Select(project => new { project.Id, project.OrgId })
                .FirstOrDefaultAsync(ct);
            return (managed?.Id, managed?.OrgId);
        }

        var projects = db.Projects.IgnoreQueryFilters().AsQueryable();
        if (preferredOrgId is { } orgId)
            projects = projects.Where(project => project.OrgId == orgId);

        var project = await projects
            .OrderBy(project => project.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return (project?.Id, project?.OrgId);
    }

    private static Task<Guid?> GetManagedProjectOrgIdAsync(
        IBeaconDb db,
        Guid userId,
        Guid projectId,
        CancellationToken ct) =>
        db.Projects.IgnoreQueryFilters()
            .Where(project => project.Id == projectId
                && db.OrgMembers.IgnoreQueryFilters().Any(member =>
                    member.OrgId == project.OrgId
                    && member.UserId == userId
                    && (member.Role == MemberRole.Owner || member.Role == MemberRole.Admin)))
            .Select(project => (Guid?)project.OrgId)
            .FirstOrDefaultAsync(ct);
}
