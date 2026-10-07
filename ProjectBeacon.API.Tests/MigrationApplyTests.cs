namespace ProjectBeacon.API.Tests;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public sealed class MigrationApplyTests
{
    [Fact]
    public async Task Migrate_FreshDatabase_LeavesNoPendingMigrations()
    {
        var url = Environment.GetEnvironmentVariable("BEACON_TEST_DATABASE_URL");
        if (string.IsNullOrEmpty(url))
            return;

        var connStr = ToNpgsql(url);
        var dbName = "beacon_migrate_test";
        var serverConn = WithDatabase(connStr, "postgres");

        await ExecAdminAsync(serverConn, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE);");
        await ExecAdminAsync(serverConn, $"CREATE DATABASE {dbName};");

        var targetConn = WithDatabase(connStr, dbName);
        var migrateOptions = new DbContextOptionsBuilder<BeaconDbContext>().UseNpgsql(targetConn).Options;
        await using (var migrateDb = new BeaconDbContext(migrateOptions))
        {
            await migrateDb.Database.MigrateAsync();
            Assert.Empty(await migrateDb.Database.GetPendingMigrationsAsync());
        }

        await ExecAdminAsync(serverConn, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE);");
    }

    private static async Task ExecAdminAsync(string serverConn, string sql)
    {
        await using var conn = new NpgsqlConnection(serverConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string WithDatabase(string connStr, string database)
    {
        var parts = connStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(';', parts.Select(p =>
            p.StartsWith("Database=", StringComparison.OrdinalIgnoreCase) ? $"Database={database}" : p));
    }

    private static string ToNpgsql(string url)
    {
        var uri = new Uri(url.Replace("postgres://", "http://", StringComparison.OrdinalIgnoreCase));
        var userInfo = uri.UserInfo.Split(':', 2);
        return $"Host={uri.Host};Port={(uri.Port > 0 ? uri.Port : 5432)};Database={uri.AbsolutePath.Trim('/')};Username={userInfo[0]};Password={(userInfo.Length > 1 ? userInfo[1] : "")}";
    }
}
