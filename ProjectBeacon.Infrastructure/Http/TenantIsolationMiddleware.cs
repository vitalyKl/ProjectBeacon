namespace ProjectBeacon.Infrastructure.Http;

using System.Security.Claims;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public sealed class TenantIsolationMiddleware
{
    private readonly RequestDelegate _next;

    public TenantIsolationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, BeaconDbContext db)
    {
        var (projectId, orgId) = await ResolveTenantScopeAsync(ctx, db);

        ctx.RequestServices?.GetService<ITenantContext>()
            ?.Assign(projectId, orgId, unscoped: false);

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

        if (ctx.User.Identity?.IsAuthenticated == true && TryGetUserId(ctx, out var userId))
        {
            var members = await LoadMembershipsAsync(db, userId);
            (projectId, orgId) = await ResolveAuthenticatedAsync(
                db, members, fromRoute, fromHeader, fromOrgRoute, fromOrgHeader, isAdmin, projectId, orgId);
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
        if (ctx.User.Identity?.IsAuthenticated == true && !isAdmin)
        {
            projectId ??= Guid.Empty;
            orgId ??= Guid.Empty;
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

        // A specific project was requested (route/header) but the user has no
        // membership in it: fail closed.
        if (requested is { } rid && !isAdmin && !IsMemberOf(members, rid))
        {
            return (Guid.Empty, orgExplicit ? orgId : Guid.Empty);
        }

        // No explicit project (route/header): resolve from the claimed project,
        // an existing membership, or the admin fallback.
        if (fromRoute is null && fromHeader is null)
        {
            var claimed = projectId;
            var claimedMember = FindMembership(members, claimed);

            if (claimedMember is not null)
            {
                return (claimedMember.ProjectId, orgExplicit ? orgId : claimedMember.Project.OrgId);
            }

            if (await ResolveAdminFallbackOrgAsync(db, isAdmin, claimed) is { } adminOrg)
            {
                return (claimed, orgExplicit ? orgId : adminOrg);
            }

            if (members.Count > 0)
            {
                return (members[0].ProjectId, orgExplicit ? orgId : members[0].Project.OrgId);
            }

            if (!isAdmin)
            {
                return (Guid.Empty, orgExplicit ? orgId : Guid.Empty);
            }

            var oldest = await db.Projects.IgnoreQueryFilters()
                .OrderBy(p => p.CreatedAt)
                .FirstOrDefaultAsync();
            return (oldest?.Id, orgId ?? oldest?.OrgId);
        }

        return (projectId, orgId);
    }

    internal static bool IsAdmin(HttpContext ctx)
        => bool.TryParse(ctx.User.FindFirstValue("isAdmin"), out var adminFlag) && adminFlag;

    internal static bool TryGetUserId(HttpContext ctx, out Guid userId)
        => Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    internal static Task<List<ProjectMember>> LoadMembershipsAsync(BeaconDbContext db, Guid userId)
        => db.ProjectMembers.IgnoreQueryFilters()
            .Include(m => m.Project)
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.JoinedAt)
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
