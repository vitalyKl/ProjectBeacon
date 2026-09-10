namespace ProjectBeacon.Application.Common;

using Infrastructure.Data;

public interface IBeaconDbContextFactory
{
    BeaconDbContext CreateDbContext();
    void DisposeContext(BeaconDbContext context);
}
