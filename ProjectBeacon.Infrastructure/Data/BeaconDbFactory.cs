namespace ProjectBeacon.Infrastructure.Data;

using Application.Persistence;
using Microsoft.EntityFrameworkCore;

// A Blazor circuit's DI scope outlives any single operation, so a scoped BeaconDbContext
// shared by concurrent async operations would hit EF's "second operation" guard.
// Handlers resolve this factory and create one context per unit of work (HandleAsync call).
/// <summary>
/// Creates a BeaconDbContext for the current tenant.
/// </summary>
public sealed class BeaconDbFactory : IDbContextFactory<BeaconDbContext>, IBeaconDbFactory
{
    private readonly DbContextOptions<BeaconDbContext> _options;
    private readonly ITenantContext? _tenant;

    public BeaconDbFactory(DbContextOptions<BeaconDbContext> options, ITenantContext? tenant)
    {
        _options = options;
        _tenant = tenant;
    }

    public BeaconDbContext CreateDbContext() => CreateDbContext(_tenant);

    public BeaconDbContext CreateDbContext(ITenantContext tenant)
    {
        var db = new BeaconDbContext(_options, tenant);
        ReconcileTenantRls(db, tenant);
        return db;
    }

    IBeaconDb IBeaconDbFactory.CreateDbContext() => CreateDbContext();

    IBeaconDb IBeaconDbFactory.CreateDbContext(ITenantContext tenant) => CreateDbContext(tenant);

    // The connection interceptor copies the AsyncLocal TenantScope into the RLS GUCs on
    // every connection open. In a long-lived circuit that AsyncLocal is a snapshot from
    // handshake time, so after the scoped tenant is rebound (membership revalidation or
    // reconnect) the GUCs are stale and RLS and the EF query filter target different
    // tenants. Reapply the GUCs from the tenant the context is bound to — the same source
    // the EF filter reads — so both layers stay on the same tenant. No-op while they agree.
    private static void ReconcileTenantRls(BeaconDbContext db, ITenantContext? tenant)
    {
        // An unscoped AsyncLocal is the explicit bootstrap/migration/worker bypass and
        // always wins: the scoped tenant is never assigned unscoped outside that path.
        if (tenant is null || TenantScope.IsUnscoped)
            return;

        if (!tenant.Unscoped
            && tenant.ProjectId == TenantScope.CurrentProjectId
            && tenant.OrgId == TenantScope.CurrentOrgId)
            return;

        TenantRlsSession.Apply(db, tenant.ProjectId, tenant.OrgId, tenant.Unscoped);
    }
}
