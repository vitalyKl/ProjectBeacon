namespace ProjectBeacon.API.Tests;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

public sealed class MigrationApplyTests
{
    [Fact]
    public async Task Migrate_FreshDatabase_LeavesNoPendingMigrations()
    {
        var url = Environment.GetEnvironmentVariable("BEACON_TEST_DATABASE_URL");

        if (!string.IsNullOrEmpty(url))
        {
            var connStr = ToNpgsql(url);
            var serverConn = connStr.Replace("Database=beacon", "Database=postgres", StringComparison.OrdinalIgnoreCase);
            var dbName = "beacon_migrate_test";

            await using (var admin = new NpgsqlConnection(serverConn))
            {
                await admin.OpenAsync();
                await using var cmd = admin.CreateCommand();
                cmd.CommandText = $"DROP DATABASE IF EXISTS {dbName}; CREATE DATABASE {dbName};";
                await cmd.ExecuteNonQueryAsync();
            }

            var targetConn = connStr.Replace("Database=beacon", $"Database={dbName}", StringComparison.OrdinalIgnoreCase);
            var migrateOptions = new DbContextOptionsBuilder<BeaconDbContext>().UseNpgsql(targetConn).Options;
            await using var migrateDb = new BeaconDbContext(migrateOptions);
            await migrateDb.Database.MigrateAsync();
            Assert.Empty(await migrateDb.Database.GetPendingMigrationsAsync());

            await using (var admin = new NpgsqlConnection(serverConn))
            {
                await admin.OpenAsync();
                await using var cmd = admin.CreateCommand();
                cmd.CommandText = $"DROP DATABASE IF EXISTS {dbName};";
                await cmd.ExecuteNonQueryAsync();
            }
            return;
        }

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

    private static string ToNpgsql(string url)
    {
        var uri = new Uri(url.Replace("postgres://", "http://", StringComparison.OrdinalIgnoreCase));
        var userInfo = uri.UserInfo.Split(':', 2);
        return $"Host={uri.Host};Port={(uri.Port > 0 ? uri.Port : 5432)};Database={uri.AbsolutePath.Trim('/')};Username={userInfo[0]};Password={(userInfo.Length > 1 ? userInfo[1] : "")}";
    }
}
