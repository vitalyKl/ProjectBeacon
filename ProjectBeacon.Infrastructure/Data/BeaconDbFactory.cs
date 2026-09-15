namespace ProjectBeacon.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;

// A Blazor circuit's DI scope outlives any single operation, so a scoped BeaconDbContext
// shared by concurrent async operations would hit EF's "second operation" guard.
// Handlers resolve this factory and create one context per unit of work (HandleAsync call).
public sealed class BeaconDbFactory : IDbContextFactory<BeaconDbContext>
{
    private readonly DbContextOptions<BeaconDbContext> _options;
    private readonly ITenantContext? _tenant;

    public BeaconDbFactory(DbContextOptions<BeaconDbContext> options, ITenantContext? tenant)
    {
        _options = options;
        _tenant = tenant;
    }

    public BeaconDbContext CreateDbContext() => new(_options, _tenant);
}
