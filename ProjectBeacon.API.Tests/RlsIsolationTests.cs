namespace ProjectBeacon.API.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
/// Row-visibility verification through a minimal-privilege role (SELECT only, no DML
/// grants) against a fully-migrated isolated database. The policies under test are the
/// ones applied by the real EF migrations — this class applies no RLS DDL of its own,
/// which keeps the check independent of the app role's grant profile.
/// </summary>
[Collection("postgres-serial")]
public sealed class RlsIsolationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string DbName = "beacon_rls_isolation";

    // xUnit instantiates this class once per test, so the heavy isolated-DB setup
    // (drop/create + full MigrateAsync) must run only once per process.
    private static bool _initialized;
    private static string s_adminConn = string.Empty;

    private readonly string _rlsRole = "rls_" + Guid.NewGuid().ToString("N");
    private readonly PostgresFixture _postgres;
    private bool _roleCreated;
    private string _adminConn = string.Empty;

    public RlsIsolationTests(PostgresFixture postgres) => _postgres = postgres;

    public async ValueTask InitializeAsync()
    {
        if (!_postgres.Available) return;

        if (!_initialized)
        {
            var adminConn = WithDatabase(_postgres.ConnectionString, DbName);
            var serverConn = WithDatabase(_postgres.ConnectionString, "postgres");

            await ExecAdminAsync(serverConn, $"DROP DATABASE IF EXISTS {DbName} WITH (FORCE);");
            await ExecAdminAsync(serverConn, $"CREATE DATABASE {DbName};");

            var migrateOptions = new DbContextOptionsBuilder<BeaconDbContext>().UseNpgsql(adminConn).Options;
            await using (var migrateDb = new BeaconDbContext(migrateOptions))
                await migrateDb.Database.MigrateAsync();

            // Mirror production: the app role must exist with its grants, even though
            // this class does not log in as it.
            var appConn = PostgresConnection.DeriveAppConnectionString(adminConn);
            await PostgresConnection.EnsureAppRoleAsync(appConn, adminConn);

            s_adminConn = adminConn;
            _initialized = true;
        }

        _adminConn = s_adminConn;

        // SELECT-only ephemeral role: a pure row-visibility probe.
        await using var conn = new NpgsqlConnection(_adminConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Exec(conn, $"CREATE ROLE {_rlsRole} LOGIN PASSWORD 'rls_test'");
        _roleCreated = true;
        await Exec(conn, $"GRANT {_rlsRole} TO CURRENT_USER");
        await Exec(conn, $"GRANT USAGE ON SCHEMA public TO {_rlsRole}");
        await Exec(conn, $"GRANT SELECT ON ALL TABLES IN SCHEMA public TO {_rlsRole}");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_roleCreated) return;

        await using var conn = new NpgsqlConnection(_adminConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Exec(conn, $"REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM {_rlsRole}");
        await Exec(conn, $"REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM {_rlsRole}");
        await Exec(conn, $"REVOKE USAGE ON SCHEMA public FROM {_rlsRole}");
        await Exec(conn, $"REVOKE {_rlsRole} FROM CURRENT_USER");
        await Exec(conn, $"DROP ROLE IF EXISTS {_rlsRole}");
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private BeaconDbContext NewAdminContext()
    {
        var options = new DbContextOptionsBuilder<BeaconDbContext>().UseNpgsql(_adminConn).Options;
        return new BeaconDbContext(options);
    }

    private async Task<(Guid ProjectId, Guid TaskId)> SeedTestDataAsync(string suffix)
    {
        // Seed as the superuser (migration role): it bypasses RLS entirely, so no
        // tenant GUCs are needed for the inserts.
        await using var seed = NewAdminContext();
        var org = Org.Create($"Rls {suffix} Org");
        seed.Orgs.Add(org);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

        var proj = Project.Create($"Rls {suffix} Project", null, org.Id);
        seed.Projects.Add(proj);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

        var task = TaskItem.Create($"Rls {suffix} Task", proj.Id, TaskPriority.Medium, TaskType.Task);
        seed.Tasks.Add(task);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (proj.Id, task.Id);
    }

    private async Task<NpgsqlConnection> OpenRlsConnectionAsync()
    {
        var conn = new NpgsqlConnection(_adminConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Exec(conn, $"SET ROLE {_rlsRole}");
        return conn;
    }

    [Fact]
    public async Task Rls_ProjectScope_HidesOtherProjectRows_ViaRawSql()
    {
        _postgres.RequirePostgres();

        var (projectA, taskA) = await SeedTestDataAsync("A");
        var (_, taskB) = await SeedTestDataAsync("B");

        await using var conn = await OpenRlsConnectionAsync();
        await using (var setCmd = new NpgsqlCommand(
            "SELECT set_config('app.tenant_project_id', @pid, false)," +
            "       set_config('app.tenant_unscoped', 'false', false)", conn))
        {
            setCmd.Parameters.AddWithValue("@pid", projectA.ToString());
            await setCmd.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        await using var cmd = new NpgsqlCommand("SELECT \"Id\" FROM \"Tasks\"", conn);
        using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken))
            ids.Add(reader.GetGuid(0));

        Assert.Contains(taskA, ids);
        // taskB belongs to a different project and must be hidden by the tenant policy.
        Assert.DoesNotContain(taskB, ids);
    }

    [Fact]
    public async Task Rls_NoScope_DeniesAllRows_ViaRawSql()
    {
        _postgres.RequirePostgres();

        var (_, taskA) = await SeedTestDataAsync("FailClosed");

        await using var conn = await OpenRlsConnectionAsync();
        await Exec(conn,
            "SELECT set_config('app.tenant_project_id', '', false)," +
            "       set_config('app.tenant_org_id', '', false)," +
            "       set_config('app.tenant_unscoped', 'false', false)");

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"Tasks\" WHERE \"Id\" = @tid", conn);
        cmd.Parameters.AddWithValue("@tid", taskA);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken: TestContext.Current.CancellationToken)!);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Rls_Unscoped_AllowsAllRows_ViaRawSql()
    {
        _postgres.RequirePostgres();

        var (_, taskA) = await SeedTestDataAsync("Unscoped");

        await using var conn = await OpenRlsConnectionAsync();
        await Exec(conn,
            "SELECT set_config('app.tenant_project_id', '', false)," +
            "       set_config('app.tenant_org_id', '', false)," +
            "       set_config('app.tenant_unscoped', 'true', false)");

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"Tasks\" WHERE \"Id\" = @tid", conn);
        cmd.Parameters.AddWithValue("@tid", taskA);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken: TestContext.Current.CancellationToken)!);
        Assert.Equal(1, count);
    }

    private static async Task ExecAdminAsync(string serverConn, string sql)
    {
        await using var conn = new NpgsqlConnection(serverConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string WithDatabase(string connStr, string database)
    {
        var parts = connStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(';', parts.Select(p =>
            p.StartsWith("Database=", StringComparison.OrdinalIgnoreCase) ? $"Database={database}" : p));
    }
}
