namespace ProjectBeacon.Infrastructure.Http;

using System.Security.Claims;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

public sealed class TenantIsolationMiddleware
{
    private readonly RequestDelegate _next;

    public TenantIsolationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, BeaconDbContext db)
    {
        var projectId = ExtractGuid(ctx, "projectId", "X-Project-Id", "project_id");
        var orgId = ExtractGuid(ctx, "orgId", "X-Org-Id", "org_id");

        var isAdmin = bool.TryParse(ctx.User.FindFirstValue("isAdmin"), out var adminFlag) && adminFlag;

        if (projectId is null && ctx.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var member = await db.ProjectMembers.IgnoreQueryFilters()
                .Include(m => m.Project)
                .Where(m => m.UserId == userId)
                .OrderBy(m => m.JoinedAt)
                .FirstOrDefaultAsync();
            if (member is not null)
            {
                projectId = member.ProjectId;
                orgId ??= member.Project.OrgId;
            }
            else if (isAdmin)
            {
                var project = await db.Projects.IgnoreQueryFilters()
                    .OrderBy(p => p.CreatedAt)
                    .FirstOrDefaultAsync();
                projectId = project?.Id;
                orgId ??= project?.OrgId;
            }
        }

        if (ctx.User.Identity?.IsAuthenticated == true && !isAdmin)
        {
            projectId ??= Guid.Empty;
            orgId ??= Guid.Empty;
        }

        IDisposable? projectScope = projectId is { } pid ? TenantScope.EnterProjectScope(pid) : null;
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

    private static Guid? ExtractGuid(HttpContext ctx, string routeKey, string header, string claimType)
    {
        var route = ctx.GetRouteValue(routeKey) as string;
        if (route is not null && Guid.TryParse(route, out var fromRoute))
            return fromRoute;

        var headerValue = ctx.Request.Headers[header].ToString();
        if (!string.IsNullOrEmpty(headerValue) && Guid.TryParse(headerValue, out var fromHeader))
            return fromHeader;

        var claim = ctx.User.FindFirst(claimType);
        if (claim is not null && Guid.TryParse(claim.Value, out var fromClaim))
            return fromClaim;

        return null;
    }
}
