namespace ProjectBeacon.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
/// <summary>
/// Design-time factory for dotnet-ef. Migrations are designed against the superuser migration
/// role, so it resolves ConnectionStrings__Migration, falling back to ConnectionStrings__Default
/// with the superuser username. Requires POSTGRES_PASSWORD unless a password is in the string.
/// </summary>
public sealed class BeaconDbContextFactory : IDesignTimeDbContextFactory<BeaconDbContext>
{
    public BeaconDbContext CreateDbContext(string[] args)
    {
        var connectionString = PostgresConnection.ResolveMigrationFromEnvironment();
        var options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new BeaconDbContext(options);
    }
}
