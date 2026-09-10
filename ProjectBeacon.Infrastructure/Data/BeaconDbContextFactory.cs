namespace ProjectBeacon.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public sealed class BeaconDbContextFactory : IDesignTimeDbContextFactory<BeaconDbContext>
{
    public BeaconDbContext CreateDbContext(string[] args)
    {
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD environment variable not set.");

        var options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql($"Host=localhost;Port=5432;Database=beacon;Username=beacon;Password={password}")
            .Options;

        return new BeaconDbContext(options);
    }
}
