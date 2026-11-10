namespace ProjectBeacon.Application.Identity;

using Application.Persistence;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Resolves a user's current tenant. Project memberships take precedence; an organization
/// membership without a project still establishes a valid organization-only tenant scope.
/// </summary>
public static class CurrentProjectLookup
{
    public static async Task<(Guid? ProjectId, Guid? OrgId)> ForUserAsync(
        IBeaconDb db,
        Guid userId,
        bool isAdmin,
        Guid? preferredProjectId = null,
        CancellationToken ct = default,
        Guid? preferredOrgId = null)
    {
        if (preferredProjectId is { } preferredProjectIdValue)
        {
            Guid? preferredOrg = null;
            if (isAdmin)
            {
                preferredOrg = await db.Projects.IgnoreQueryFilters()
                    .Where(project => project.Id == preferredProjectIdValue)
                    .Select(project => (Guid?)project.OrgId)
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                preferredOrg = await db.ProjectMembers.IgnoreQueryFilters()
                    .Where(member => member.UserId == userId
                        && member.ProjectId == preferredProject
                        && member.Project.Org.LifecycleStatus == OrganizationLifecycleStatus.Active)
                    .Select(member => (Guid?)member.Project.OrgId)
                    .FirstOrDefaultAsync(ct);
            }

            if (preferredOrg is { } claimedOrg)
                return (preferredProjectIdValue, claimedOrg);
        }

        if (preferredOrgId is { } preferredOrganization && preferredOrganization != Guid.Empty)
        {
            var preferredProjectRow = await db.ProjectMembers.IgnoreQueryFilters()
                .Where(member => member.UserId == userId
                    && member.Project.OrgId == preferredOrganization
                    && member.Project.Org.LifecycleStatus == OrganizationLifecycleStatus.Active)
                .OrderBy(member => member.JoinedAt)
                .Select(member => new { member.ProjectId, member.Project.OrgId })
                .FirstOrDefaultAsync(ct);
            if (preferredProjectRow is not null)
                return (preferredProjectRow.ProjectId, preferredProjectRow.OrgId);

            var stillOrganizationMember = await db.OrgMembers.IgnoreQueryFilters()
                .AnyAsync(member => member.UserId == userId && member.OrgId == preferredOrganization, ct);
            if (stillOrganizationMember)
                return (null, preferredOrganization);
        }

        // Prefer an active project membership for users who can work in projects.
        var activeProjectMember = await db.ProjectMembers.IgnoreQueryFilters()
            .Where(member => member.UserId == userId
                && member.Project.Org.LifecycleStatus == OrganizationLifecycleStatus.Active)
            .OrderBy(member => member.JoinedAt)
            .Select(member => new { member.ProjectId, member.Project.OrgId })
            .FirstOrDefaultAsync(ct);
        if (activeProjectMember is not null)
            return (activeProjectMember.ProjectId, activeProjectMember.OrgId);

        // An organization owner/admin may be invited before any project membership exists.
        // Such a user still needs an org-scoped tenant for onboarding, billing and invitations.
        var activeOrgMember = await db.OrgMembers.IgnoreQueryFilters()
            .Where(member => member.UserId == userId
                && member.Org.LifecycleStatus == OrganizationLifecycleStatus.Active)
            .OrderBy(member => member.JoinedAt)
            .Select(member => (Guid?)member.OrgId)
            .FirstOrDefaultAsync(ct);
        if (activeOrgMember is { } activeOrgId)
            return (null, activeOrgId);

        // Preserve a suspended/closed membership as the selected tenant so lifecycle-aware
        // filters can deny data access without invalidating the user's global login session.
        var existingProjectMember = await db.ProjectMembers.IgnoreQueryFilters()
            .Where(member => member.UserId == userId)
            .OrderBy(member => member.JoinedAt)
            .Select(member => new { member.ProjectId, member.Project.OrgId })
            .FirstOrDefaultAsync(ct);
        if (existingProjectMember is not null)
            return (existingProjectMember.ProjectId, existingProjectMember.OrgId);

        var existingOrgMember = await db.OrgMembers.IgnoreQueryFilters()
            .Where(member => member.UserId == userId)
            .OrderBy(member => member.JoinedAt)
            .Select(member => (Guid?)member.OrgId)
            .FirstOrDefaultAsync(ct);
        if (existingOrgMember is { } existingOrgId)
            return (null, existingOrgId);

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
