namespace ProjectBeacon.API.Tests;

using Application.Authorization;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>
/// Data Protection key ring persisted in Postgres (DataProtectionKeys) and sealed with an
/// AES-GCM key derived from DATA_PROTECTION_KEY. Exercises the Postgres-backed XML repository,
/// the sealing layer, the startup guard, and the tenant-less RLS posture of the ring table
/// against a fully-migrated isolated database under the real beacon_app role.
/// </summary>
[Collection("postgres-serial")]
public sealed class DataProtectionRingTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string DbName = "beacon_dp_ring";
    private const string RaceDbName = "beacon_dp_ring_race";
    private const string DataKey = "Ring-Test-Data-Protection-Key-min-32-chars";
    private const string WrongKey = "Other-Test-Data-Protection-Key-min-32-chars";

    // xUnit instantiates this class once per test; the isolated-DB setup runs once per
    // process (same pattern as TenantlessCreateRlsTests).
    private static bool _initialized;
    private static string s_adminConn = string.Empty;
    private static string s_appConn = string.Empty;
    private static string s_raceAdminConn = string.Empty;
    private static string s_raceAppConn = string.Empty;

    private readonly PostgresFixture _postgres;
    private string _adminConn = string.Empty;
    private string _appConn = string.Empty;
    private string _raceAdminConn = string.Empty;
    private string _raceAppConn = string.Empty;

    public DataProtectionRingTests(PostgresFixture postgres) => _postgres = postgres;

    public async ValueTask InitializeAsync()
    {
        if (!_postgres.Available) return;

        if (!_initialized)
        {
            var serverConn = WithDatabase(_postgres.ConnectionString, "postgres");

            foreach (var db in new[] { DbName, RaceDbName })
            {
                await ExecAdminAsync(serverConn, $"DROP DATABASE IF EXISTS {db} WITH (FORCE);");
                await ExecAdminAsync(serverConn, $"CREATE DATABASE {db};");
            }

            var adminConn = WithDatabase(_postgres.ConnectionString, DbName);
            var migrateOptions = new DbContextOptionsBuilder<BeaconDbContext>().UseNpgsql(adminConn).Options;
            await using (var migrateDb = new BeaconDbContext(migrateOptions))
                await migrateDb.Database.MigrateAsync();

            // One org-scoped project row owned by no tenant: it must stay invisible to
            // beacon_app without the tenant GUCs, while the ring table (no RLS policy)
            // stays fully usable.
            await using (var seed = new BeaconDbContext(migrateOptions))
            {
                using (TenantScope.EnterUnscoped())
                {
                    var org = Org.Create("DP Ring Org");
                    seed.Orgs.Add(org);
                    await seed.SaveChangesAsync();
                    seed.Projects.Add(Project.Create("DP Ring Project", null, org.Id));
                    await seed.SaveChangesAsync();
                }
            }

            var appConn = PostgresConnection.DeriveAppConnectionString(adminConn);
            await PostgresConnection.EnsureAppRoleAsync(appConn, adminConn);

            var raceAdminConn = WithDatabase(_postgres.ConnectionString, RaceDbName);
            var raceMigrateOptions = new DbContextOptionsBuilder<BeaconDbContext>().UseNpgsql(raceAdminConn).Options;
            await using (var raceDb = new BeaconDbContext(raceMigrateOptions))
                await raceDb.Database.MigrateAsync();

            var raceAppConn = PostgresConnection.DeriveAppConnectionString(raceAdminConn);
            await PostgresConnection.EnsureAppRoleAsync(raceAppConn, raceAdminConn);

            s_adminConn = adminConn;
            s_appConn = appConn;
            s_raceAdminConn = raceAdminConn;
            s_raceAppConn = raceAppConn;
            _initialized = true;
        }

        _adminConn = s_adminConn;
        _appConn = s_appConn;
        _raceAdminConn = s_raceAdminConn;
        _raceAppConn = s_raceAppConn;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // (d) A protect/unprotect round trip succeeds and every stored key row carries the seal:
    // the raw <data> element (which holds the key material) must not survive in the database.
    [Fact]
    public async Task RingRoundTripSealsEveryKeyRow()
    {
        _postgres.RequirePostgres();
        await TruncateAsync(_adminConn);

        await using var provider = BuildProvider(_appConn, DataKey);
        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("dp-ring-sentinel");
        var blob = protector.Protect("sentinel-payload");
        Assert.Equal("sentinel-payload", protector.Unprotect(blob));

        var rows = await SelectXmlRowsAsync(_adminConn);
        Assert.NotEmpty(rows);
        foreach (var xml in rows)
        {
            Assert.Contains("kr1:", xml);
            Assert.DoesNotContain("<data", xml);
        }
    }

    // (e1) A single flipped byte inside the seal must make the startup guard fail closed.
    [Fact]
    public async Task StartupGuardRejectsTamperedKeyRow()
    {
        _postgres.RequirePostgres();
        await TruncateAsync(_adminConn);

        await using (var provider = BuildProvider(_appConn, DataKey))
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("dp-ring-tamper").Protect("x");

        var (keyId, original) = await FlipSealCharAsync(_adminConn);
        try
        {
            using var guardProvider = BuildProvider(_appConn, DataKey);
            Assert.Throws<InvalidOperationException>(() => guardProvider.AssertRingHealthy(requireKeys: true));
        }
        finally
        {
            await RestoreRowAsync(_adminConn, keyId, original);
        }
    }

    // (e2) A ring sealed with a different DATA_PROTECTION_KEY must make the guard fail closed.
    [Fact]
    public async Task StartupGuardRejectsWrongSealingKey()
    {
        _postgres.RequirePostgres();
        await TruncateAsync(_adminConn);

        await using (var provider = BuildProvider(_appConn, DataKey))
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("dp-ring-wrong-key").Protect("x");

        using var guardProvider = BuildProvider(_appConn, WrongKey);
        Assert.Throws<InvalidOperationException>(() => guardProvider.AssertRingHealthy(requireKeys: true));
    }

    // (f) beacon_app without the tenant GUCs sees no Projects rows (RLS fail-closed) but
    // can read and write the ring table, which has no RLS policy.
    [Fact]
    public async Task AppRoleWithoutTenantGucCannotSeeProjectsButCanUseRingTable()
    {
        _postgres.RequirePostgres();

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync();

        Assert.Equal(0L, (long)(await ScalarAsync(conn, "SELECT count(*) FROM \"Projects\";")));

        const string probeXml = "<probe/>";
        var keyId = Guid.NewGuid().ToString();
        await ExecCommandAsync(conn,
            "INSERT INTO \"DataProtectionKeys\" (\"KeyId\", \"XmlData\") VALUES (@p1, @p2);", keyId, probeXml);

        Assert.Equal(probeXml, (string)(await ScalarAsync(conn,
            "SELECT \"XmlData\" FROM \"DataProtectionKeys\" WHERE \"KeyId\" = @p1;", keyId)));

        await ExecCommandAsync(conn, "DELETE FROM \"DataProtectionKeys\" WHERE \"KeyId\" = @p1;", keyId);
        Assert.Equal(0L, (long)(await ScalarAsync(conn,
            "SELECT count(*) FROM \"DataProtectionKeys\" WHERE \"KeyId\" = @p1;", keyId)));
    }

    // (g) Two parallel first starts on an empty ring must seed exactly one key row.
    [Fact]
    public async Task ParallelFirstStartsOnEmptyRingSeedExactlyOneKey()
    {
        _postgres.RequirePostgres();
        await TruncateAsync(_raceAdminConn);

        await using var p1 = BuildProvider(_raceAppConn, DataKey);
        await using var p2 = BuildProvider(_raceAppConn, DataKey);
        await Task.WhenAll(p1.SeedPrimaryKeyAsync(), p2.SeedPrimaryKeyAsync());

        await using var conn = new NpgsqlConnection(_raceAdminConn);
        await conn.OpenAsync();
        Assert.Equal(1L, (long)(await ScalarAsync(conn, "SELECT count(*) FROM \"DataProtectionKeys\";")));
    }

    private static ServiceProvider BuildProvider(string appConn, string dataKey)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATA_PROTECTION_KEY"] = dataKey,
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["ConnectionStrings:Default"] = appConn,
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // Build() returns IConfigurationRoot; register the IConfiguration base type
        // explicitly so the ring wiring can resolve it.
        services.AddSingleton<IConfiguration>(config);
        services.AddDbContext<BeaconDbContext>(o => o.UseNpgsql(appConn));
        services.AddBeaconDataProtection(config, "Testing");
        return services.BuildServiceProvider();
    }

    private static async Task TruncateAsync(string adminConn) =>
        await ExecAdminAsync(adminConn, "DELETE FROM \"DataProtectionKeys\";");

    private static async Task<List<string>> SelectXmlRowsAsync(string adminConn)
    {
        await using var conn = new NpgsqlConnection(adminConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT \"XmlData\" FROM \"DataProtectionKeys\" ORDER BY \"KeyId\";", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    // Flips the first base64 character of the seal payload in one sealed row and returns
    // the row id plus the original XML so the caller can restore it.
    private static async Task<(string KeyId, string Original)> FlipSealCharAsync(string adminConn)
    {
        await using var conn = new NpgsqlConnection(adminConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT \"KeyId\", \"XmlData\" FROM \"DataProtectionKeys\" WHERE \"XmlData\" LIKE '%kr1:%' LIMIT 1;", conn);
        string keyId;
        var xml = string.Empty;
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("No sealed key row found to tamper with.");
            keyId = reader.GetString(0);
            xml = reader.GetString(1);
        }

        var index = xml.IndexOf("kr1:", StringComparison.Ordinal) + 4;
        var flipped = xml[index] == 'A' ? 'B' : 'A';
        var tampered = xml[..index] + flipped + xml[(index + 1)..];

        await ExecCommandAsync(conn, "UPDATE \"DataProtectionKeys\" SET \"XmlData\" = @p1 WHERE \"KeyId\" = @p2;",
            tampered, keyId);

        return (keyId, xml);
    }

    private static async Task RestoreRowAsync(string adminConn, string keyId, string xml)
    {
        await using var conn = new NpgsqlConnection(adminConn);
        await conn.OpenAsync();
        await ExecCommandAsync(conn, "UPDATE \"DataProtectionKeys\" SET \"XmlData\" = @p1 WHERE \"KeyId\" = @p2;",
            xml, keyId);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql, params object?[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        for (var i = 0; i < parameters.Length; i++)
            cmd.Parameters.Add(new NpgsqlParameter($"@p{i + 1}", parameters[i] ?? DBNull.Value));
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecCommandAsync(NpgsqlConnection conn, string sql, params object?[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        for (var i = 0; i < parameters.Length; i++)
            cmd.Parameters.Add(new NpgsqlParameter($"@p{i + 1}", parameters[i] ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync();
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
