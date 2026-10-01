namespace ProjectBeacon.API.Tests;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

public sealed class MigrationApplyTests
{
    [Fact]
    public async Task Migrate_FreshDatabase_LeavesNoPendingMigrations()
    {
        await using var container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();
        await container.StartAsync();

        var options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .Options;
        await using var db = new BeaconDbContext(options);
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
