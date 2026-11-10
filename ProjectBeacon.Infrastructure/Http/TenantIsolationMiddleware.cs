namespace ProjectBeacon.Infrastructure.Http;

using System.Security.Claims;
using Application.Authorization;
using Application.Persistence;
using ProjectBeacon.Domain.Enums;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
/// <summary>
/// Resolves the caller's project and org, stores them on the tenant context, and applies both the EF filter and the Postgres RLS settings for the rest of the request.
/// </summary>
public sealed class TenantIsolationMiddleware
{
    private readonly RequestDelegate _next;

    public TenantIsolationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, BeaconDbContext db)
    {
        Guid? projectId;
        Guid? orgId;
        var tenantContext = ctx.RequestServices?.GetService<ITenantContext>();

        using (TenantScope.EnterUnscoped())
        {
            await TenantRlsSession.ApplyAsync(db, null, null, unscoped: true);
            (projectId, orgId) = await ResolveTenantScopeAsync(ctx, db);
            tenantContext?.Assign(projectId, orgId, unscoped: false);

            OrganizationLifecycleStatus? lifecycleStatus = null;
            if (orgId is { } resolvedOrgId && resolvedOrgId != Guid.Empty)
            {
                lifecycleStatus = await db.Orgs.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(org => org.Id == resolvedOrgId)
                    .Select(org => (OrganizationLifecycleStatus?)org.LifecycleStatus)
                    .FirstOrDefaultAsync(ctx.RequestAborted);
            }
            tenantContext?.SetLifecycleStatus(lifecycleStatus, enforced: orgId is { } lifecycleOrgId && lifecycleOrgId != Guid.Empty);
        }

        await TenantRlsSession.ApplyAsync(db, projectId, orgId, unscoped: false);

        IDisposable? projectScope = projectId is { } projectPid ? TenantScope.EnterProjectScope(projectPid) : null;
        IDisposable? orgScope = orgId is { } oid ? TenantScope.EnterOrgScope(oid) : null;
        try
        {
            await _next(ctx);
        }
        finally
        {
            projectScope?.Dispose();
            orgScope?.Dispose();
        }
    }

    private static async Task<(Guid? ProjectId, Guid? OrgId)> ResolveTenantScopeAsync(HttpContext ctx, BeaconDbContext db)
    {
        var fromRoute = FromRoute(ctx, "projectId");
        var fromHeader = FromHeader(ctx, "X-Project-Id");
        var fromOrgRoute = FromRoute(ctx, "orgId");
        var fromOrgHeader = FromHeader(ctx, "X-Org-Id");
        var projectId = fromRoute ?? fromHeader ?? FromClaim(ctx, "project_id");
        var orgId = fromOrgRoute ?? fromOrgHeader ?? FromClaim(ctx, "org_id");

        var isAdmin = IsAdmin(ctx);

        // API tokens are bound to a single project: the token's project claim
        // is the scope. Ignore any route/header project override and fail
        // closed if it disagrees with the binding.
        if (ctx.User.Identity?.AuthenticationType == ActorContextFactory.ApiTokenAuthType)
        {
            var bound = FromClaim(ctx, "project_id") ?? Guid.Empty;
            var overridden = fromRoute ?? fromHeader;
            if (bound == Guid.Empty || (overridden is { } requestedProject && requestedProject != bound))
                return (Guid.Empty, Guid.Empty);

            // API tokens are project-bound. An org header must not move their RLS scope
            // away from the organization that owns the token's bound project.
            var boundOrg = await GetProjectOrgIdAsync(db, bound);
            if (boundOrg is null)
                return (Guid.Empty, Guid.Empty);
            var requestedOrg = fromOrgRoute ?? fromOrgHeader;
            if (requestedOrg is { } orgOverride && orgOverride != boundOrg)
                return (Guid.Empty, Guid.Empty);

            return (bound, boundOrg);
        }

        if (ctx.User.Identity?.IsAuthenticated == true && TryGetUserId(ctx, out var userId))
        {
            var members = await LoadMembershipsAsync(db, userId);
            var orgMembers = await LoadOrganizationMembershipsAsync(db, userId);
            (projectId, orgId) = await ResolveAuthenticatedAsync(
                db, members, orgMembers, fromRoute, fromHeader, fromOrgRoute, fromOrgHeader, isAdmin, projectId, orgId);
        }

        // If a specific project was requested (route/header) but no explicit
        // org was given, derive the org from the project.  This prevents the
        // stale `org_id` claim in the JWT from pinning the scope to the
        // org the user was "on" at login time, which would mismatch the
        // explicitly requested project and cause "Project not found."
        if (fromOrgRoute is null && fromOrgHeader is null
            && (fromRoute is { } || fromHeader is { })
            && projectId is { } requested && requested != Guid.Empty)
        {
            orgId = await GetProjectOrgIdAsync(db, requested) ?? orgId;
        }

        // Single fail-closed exit: an authenticated non-admin never resolves to a null scope.
        if (ctx.User.Identity?.IsAuthenticated == true && !isAdmin
            && projectId is null && orgId is null)
        {
            projectId = Guid.Empty;
            orgId = Guid.Empty;
        }

        if (projectId is { } resolvedProject && resolvedProject != Guid.Empty && orgId is null)
        {
            orgId = await GetProjectOrgIdAsync(db, resolvedProject);
        }

        return (projectId, orgId);
    }

    private static async Task<(Guid? ProjectId, Guid? OrgId)> ResolveAuthenticatedAsync(
        BeaconDbContext db,
        List<ProjectMember> members,
        List<OrgMember> orgMembers,
        Guid? fromRoute,
        Guid? fromHeader,
        Guid? fromOrgRoute,
        Guid? fromOrgHeader,
        bool isAdmin,
        Guid? projectId,
        Guid? orgId)
    {
        var orgExplicit = fromOrgRoute is not null || fromOrgHeader is not null;
        var requested = fromRoute ?? fromHeader;

        // Organization-only requests are valid for a real organization member, but a
        // client-supplied org id must never broaden a non-admin's tenant scope.
        if (orgExplicit && !isAdmin
            && (orgId is null || orgId == Guid.Empty || orgMembers.All(member => member.OrgId != orgId)))
        {
            return (Guid.Empty, Guid.Empty);
        }

        // A specific project was requested (route/header) but the user has no
        // membership in it: fail closed. When both project and org are explicit,
        // their organization scopes must agree.
        if (requested is { } rid && !isAdmin)
        {
            var requestedMember = members.FirstOrDefault(member => member.ProjectId == rid);
            if (requestedMember is null
                || (orgExplicit && requestedMember.Project.OrgId != orgId))
                return (Guid.Empty, Guid.Empty);
        }

        if (requested is null && orgExplicit)
            return (null, orgId);

        // No explicit project (route/header): resolve from the claimed project,
        // a valid organization claim, an existing membership, or the admin fallback.
        if (fromRoute is null && fromHeader is null)
        {
            var claimed = projectId;
            var claimedMember = FindMembership(members, claimed);

            if (claimedMember is not null)
                return (claimedMember.ProjectId, claimedMember.Project.OrgId);

            if (orgId is { } claimedOrg && claimedOrg != Guid.Empty
                && orgMembers.Any(member => member.OrgId == claimedOrg))
                return (null, claimedOrg);

            if (await ResolveAdminFallbackOrgAsync(db, isAdmin, claimed) is { } adminOrg)
                return (claimed, adminOrg);

            if (members.Count > 0)
                return (members[0].ProjectId, members[0].Project.OrgId);

            if (orgMembers.Count > 0)
                return (null, orgMembers[0].OrgId);

            if (!isAdmin)
                return (Guid.Empty, Guid.Empty);

            var oldest = await db.Projects.IgnoreQueryFilters()
                .OrderBy(project => project.CreatedAt)
                .FirstOrDefaultAsync();
            return (oldest?.Id, orgId ?? oldest?.OrgId);
        }

        return (projectId, orgId);
    }

    internal static bool IsAdmin(HttpContext ctx)
        => ActorContextFactory.FromPrincipal(ctx.User).IsAdmin;

    internal static bool TryGetUserId(HttpContext ctx, out Guid userId)
    {
        userId = ActorContextFactory.FromPrincipal(ctx.User).UserId ?? Guid.Empty;
        return !userId.Equals(Guid.Empty);
    }

    internal static Task<List<ProjectMember>> LoadMembershipsAsync(BeaconDbContext db, Guid userId)
        => db.ProjectMembers.IgnoreQueryFilters()
            .Include(m => m.Project)
                .ThenInclude(project => project.Org)
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Project.Org.LifecycleStatus == OrganizationLifecycleStatus.Active ? 0 : 1)
            .ThenBy(m => m.JoinedAt)
            .ToListAsync();

    internal static Task<List<OrgMember>> LoadOrganizationMembershipsAsync(BeaconDbContext db, Guid userId)
        => db.OrgMembers.IgnoreQueryFilters()
            .Include(member => member.Org)
            .Where(member => member.UserId == userId)
            .OrderBy(member => member.Org.LifecycleStatus == OrganizationLifecycleStatus.Active ? 0 : 1)
            .ThenBy(member => member.JoinedAt)
            .ToListAsync();

    internal static bool IsMemberOf(IReadOnlyList<ProjectMember> members, Guid projectId)
        => members.Any(m => m.ProjectId == projectId);

    internal static ProjectMember? FindMembership(IReadOnlyList<ProjectMember> members, Guid? projectId)
        => projectId is { } pid ? members.FirstOrDefault(m => m.ProjectId == pid) : null;

    internal static Task<Guid?> ResolveAdminFallbackOrgAsync(BeaconDbContext db, bool isAdmin, Guid? claimed)
    {
        if (!isAdmin || claimed is not { } ac)
            return Task.FromResult<Guid?>(null);
        return db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == ac)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync();
    }

    internal static Task<Guid?> GetProjectOrgIdAsync(BeaconDbContext db, Guid projectId)
        => db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync();

    internal static Guid? FromRoute(HttpContext ctx, string routeKey)
    {
        var route = ctx.GetRouteValue(routeKey) as string;
        return route is not null && Guid.TryParse(route, out var id) ? id : null;
    }

    internal static Guid? FromHeader(HttpContext ctx, string header)
    {
        var value = ctx.Request.Headers[header].ToString();
        return !string.IsNullOrEmpty(value) && Guid.TryParse(value, out var id) ? id : null;
    }

    internal static Guid? FromClaim(HttpContext ctx, string claimType)
    {
        var claim = ctx.User.FindFirst(claimType);
        return claim is not null && Guid.TryParse(claim.Value, out var id) ? id : null;
    }
}
