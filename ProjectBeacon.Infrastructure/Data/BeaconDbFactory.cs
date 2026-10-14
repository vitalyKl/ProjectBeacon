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

    public BeaconDbContext CreateDbContext()
    {
        var db = new BeaconDbContext(_options, _tenant);
        ReconcileTenantRls(db);
        return db;
    }

    IBeaconDb IBeaconDbFactory.CreateDbContext() => CreateDbContext();

    // The connection interceptor copies the AsyncLocal TenantScope into the RLS GUCs on
    // every connection open. In a long-lived circuit that AsyncLocal is a snapshot from
    // handshake time, so after the scoped tenant is rebound (membership revalidation or
    // reconnect) the GUCs are stale and RLS and the EF query filter target different
    // tenants. Reapply the GUCs from the scoped tenant — the same source the EF filter
    // reads — so both layers stay on the same tenant. No-op while they agree.
    private void ReconcileTenantRls(BeaconDbContext db)
    {
        // An unscoped AsyncLocal is the explicit bootstrap/migration/worker bypass and
        // always wins: the scoped tenant is never assigned unscoped outside that path.
        if (_tenant is null || TenantScope.IsUnscoped)
            return;

        if (!_tenant.Unscoped
            && _tenant.ProjectId == TenantScope.CurrentProjectId
            && _tenant.OrgId == TenantScope.CurrentOrgId)
            return;

        TenantRlsSession.Apply(db, _tenant.ProjectId, _tenant.OrgId, _tenant.Unscoped);
    }
}
