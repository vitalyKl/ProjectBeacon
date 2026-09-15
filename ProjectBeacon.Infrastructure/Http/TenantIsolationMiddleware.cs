namespace ProjectBeacon.Infrastructure.Http;

using System.Security.Claims;
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
        var fromRoute = FromRoute(ctx, "projectId");
        var fromHeader = FromHeader(ctx, "X-Project-Id");
        var fromOrgRoute = FromRoute(ctx, "orgId");
        var fromOrgHeader = FromHeader(ctx, "X-Org-Id");
        var projectId = fromRoute ?? fromHeader ?? FromClaim(ctx, "project_id");
        var orgId = fromOrgRoute ?? fromOrgHeader ?? FromClaim(ctx, "org_id");

        var isAdmin = bool.TryParse(ctx.User.FindFirstValue("isAdmin"), out var adminFlag) && adminFlag;

        if (ctx.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var members = await db.ProjectMembers.IgnoreQueryFilters()
                .Include(m => m.Project)
                .Where(m => m.UserId == userId)
                .OrderBy(m => m.JoinedAt)
                .ToListAsync();
            var requested = fromRoute ?? fromHeader;
            if (requested is { } rid && !isAdmin && members.TrueForAll(m => m.ProjectId != rid))
            {
                projectId = Guid.Empty;
                if (fromOrgRoute is null && fromOrgHeader is null)
                    orgId = Guid.Empty;
            }
            else if (fromRoute is null && fromHeader is null)
            {
                var claimed = projectId;
                var claimedMember = claimed is { } cid ? members.FirstOrDefault(m => m.ProjectId == cid) : null;
                if (claimedMember is not null)
                {
                    projectId = claimedMember.ProjectId;
                    if (fromOrgRoute is null && fromOrgHeader is null)
                        orgId = claimedMember.Project.OrgId;
                }
                else
                {
                    var adminClaimed = isAdmin ? claimed : null;
                    var adminOrg = adminClaimed is { } ac
                        ? await db.Projects.IgnoreQueryFilters()
                            .Where(p => p.Id == ac)
                            .Select(p => (Guid?)p.OrgId)
                            .FirstOrDefaultAsync()
                        : null;
                    if (adminOrg is { } org)
                    {
                        projectId = adminClaimed;
                        if (fromOrgRoute is null && fromOrgHeader is null)
                            orgId = org;
                    }
                    else
                    {
                        projectId = null;
                    }
                }

                if (projectId is null)
                {
                    if (members.Count > 0)
                    {
                        projectId = members[0].ProjectId;
                        if (fromOrgRoute is null && fromOrgHeader is null)
                            orgId = members[0].Project.OrgId;
                    }
                    else if (!isAdmin)
                    {
                        projectId = Guid.Empty;
                        if (fromOrgRoute is null && fromOrgHeader is null)
                            orgId = Guid.Empty;
                    }
                    else
                    {
                        var project = await db.Projects.IgnoreQueryFilters()
                            .OrderBy(p => p.CreatedAt)
                            .FirstOrDefaultAsync();
                        projectId = project?.Id;
                        orgId ??= project?.OrgId;
                    }
                }
            }
        }

        // If a specific project was requested (route/header) but no explicit
        // org was given, derive the org from the project.  This prevents the
        // stale `org_id` claim in the JWT from pinning the scope to the
        // org the user was "on" at login time, which would mismatch the
        // explicitly requested project and cause "Project not found."
        if (fromOrgRoute is null && fromOrgHeader is null
            && (fromRoute is { } || fromHeader is { })
            && projectId is { } pid && pid != Guid.Empty)
        {
            orgId = await db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == pid)
                .Select(p => (Guid?)p.OrgId)
                .FirstOrDefaultAsync() ?? orgId;
        }

        if (ctx.User.Identity?.IsAuthenticated == true && !isAdmin)
        {
            projectId ??= Guid.Empty;
            orgId ??= Guid.Empty;
        }

        if (projectId is { } resolvedProject && resolvedProject != Guid.Empty && orgId is null)
        {
            orgId = await db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == resolvedProject)
                .Select(p => (Guid?)p.OrgId)
                .FirstOrDefaultAsync();
        }

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

    private static Guid? FromRoute(HttpContext ctx, string routeKey)
    {
        var route = ctx.GetRouteValue(routeKey) as string;
        return route is not null && Guid.TryParse(route, out var id) ? id : null;
    }

    private static Guid? FromHeader(HttpContext ctx, string header)
    {
        var value = ctx.Request.Headers[header].ToString();
        return !string.IsNullOrEmpty(value) && Guid.TryParse(value, out var id) ? id : null;
    }

    private static Guid? FromClaim(HttpContext ctx, string claimType)
    {
        var claim = ctx.User.FindFirst(claimType);
        return claim is not null && Guid.TryParse(claim.Value, out var id) ? id : null;
    }
}
