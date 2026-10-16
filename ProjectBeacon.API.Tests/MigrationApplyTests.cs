namespace ProjectBeacon.API.Tests;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
/// Applies migrations to a fresh database on the fixture's server (a disposable
/// Testcontainers container, or an explicitly supplied BEACON_TEST_DATABASE_URL
/// instance) and verifies nothing is left pending.
/// </summary>
[Collection("postgres-serial")]
public sealed class MigrationApplyTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public MigrationApplyTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Migrate_FreshDatabase_LeavesNoPendingMigrations()
    {
        _postgres.RequirePostgres();

        var dbName = "beacon_migrate_test";
        var serverConn = WithDatabase(_postgres.ConnectionString, "postgres");

        await ExecAdminAsync(serverConn, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE);");
        await ExecAdminAsync(serverConn, $"CREATE DATABASE {dbName};");

        var targetConn = WithDatabase(_postgres.ConnectionString, dbName);
        var migrateOptions = new DbContextOptionsBuilder<BeaconDbContext>().UseNpgsql(targetConn).Options;
        await using (var migrateDb = new BeaconDbContext(migrateOptions))
        {
            await migrateDb.Database.MigrateAsync(TestContext.Current.CancellationToken);
            Assert.Empty(await migrateDb.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
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
}
