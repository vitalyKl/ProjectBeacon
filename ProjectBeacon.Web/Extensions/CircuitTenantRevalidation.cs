using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Identity;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Security;

namespace ProjectBeacon.Web.Extensions;

/// <summary>
/// Live identity/tenant check for long-lived InteractiveServer circuits. The circuit's
/// principal is a snapshot from the handshake, so membership and account state are
/// re-checked against the database. When the circuit's tenant no longer matches a
/// live membership, the scoped <see cref="ITenantContext"/> is rebound to the user's
/// oldest remaining membership (fail-closed to the scoped tenant until then).
/// Returns false when the circuit must be signed out.
/// </summary>
public static class CircuitTenantRevalidation
{
    public static async Task<bool> RevalidateAsync(
        IServiceProvider scope, ClaimsPrincipal? user, ILogger? logger, CancellationToken ct = default)
    {
        var tenant = scope.GetService<ITenantContext>();
        var dbFactory = scope.GetService<IBeaconDbFactory>();
        if (tenant is null || dbFactory is null)
            return true;

        var actor = ActorContextFactory.FromPrincipal(user);
        if (actor.UserId is null)
            return false;

        // The scoped assignment wins over the AsyncLocal captured at handshake time,
        // so read the scoped value first to get the tenant the circuit is actually using.
        var currentProjectId = tenant.Unscoped ? null : (tenant.ProjectId ?? TenantScope.CurrentProjectId);
        var currentOrgId = tenant.Unscoped ? null : tenant.OrgId;
        // Guid.Empty is the fail-closed sentinel applied when a user has no tenant membership.
        if (currentProjectId == Guid.Empty)
            currentProjectId = null;
        if (currentOrgId == Guid.Empty)
            currentOrgId = null;

        try
        {
            // Revalidate disable state and SessionVersion as well as membership. The shared
            // per-process cache bounds the stale-session window to 30 seconds.
            // The circuit's captured tenant scope is stale; the membership check must run
            // unscoped so RLS does not mask memberships in other projects.
            using (TenantScope.EnterUnscoped())
            {
                if (!await UserSessionVersionValidator.ValidateAsync(scope, user, ct))
                    return false;

                await using var db = dbFactory.CreateDbContext();
                if (!await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == actor.UserId.Value, ct))
                    return false;

                var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(
                    db,
                    actor.UserId.Value,
                    actor.IsAdmin,
                    currentProjectId,
                    ct,
                    currentOrgId);

                // A user may legitimately have organization membership before being added
                // to any project. Keep that org-only session; sign out only when a previously
                // scoped user has lost every remaining project and organization membership.
                if (projectId is null && orgId is null)
                    return currentProjectId is null && currentOrgId is null;

                if (!tenant.Unscoped)
                {
                    tenant.Assign(projectId, orgId, unscoped: false);
                    OrganizationLifecycleStatus? lifecycleStatus = null;
                    if (orgId is { } resolvedOrgId && resolvedOrgId != Guid.Empty)
                    {
                        lifecycleStatus = await db.Orgs.IgnoreQueryFilters()
                            .AsNoTracking()
                            .Where(org => org.Id == resolvedOrgId)
                            .Select(org => (OrganizationLifecycleStatus?)org.LifecycleStatus)
                            .FirstOrDefaultAsync(ct);
                    }
                    tenant.SetLifecycleStatus(
                        lifecycleStatus,
                        enforced: orgId is { } tenantOrgId && tenantOrgId != Guid.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            // A transient database failure must not force sign-out; the stale scope
            // stays fail-closed and the next tick retries.
            logger?.LogWarning(ex, "Circuit tenant revalidation failed for user {UserId}; keeping session", actor.UserId);
            return true;
        }

        return true;
    }
}
