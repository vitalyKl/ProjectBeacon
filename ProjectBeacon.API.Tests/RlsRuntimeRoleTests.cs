namespace ProjectBeacon.API.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
/// End-to-end RLS verification against a fully-migrated isolated database, using the real runtime
/// role (beacon_app) as the login. Proves RLS (not just the EF query filter) enforces tenant
/// isolation for the non-superuser the application connects as.
/// </summary>
[Collection("postgres-serial")]
public sealed class RlsRuntimeRoleTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string DbName = "beacon_rls_runtime";

    // xUnit instantiates this class once per test, so the heavy isolated-DB setup
    // (drop/create + full MigrateAsync + seed) must run only once per process. The
    // static guard makes InitializeAsync idempotent across the 5 test instances.
    private static bool _initialized;
    private static string s_adminConn = string.Empty;
    private static string s_appConn = string.Empty;
    private static Guid s_projectA, s_taskA1, s_taskA2, s_taskB;

    private readonly PostgresFixture _postgres;
    private string _adminConn = string.Empty;
    private string _appConn = string.Empty;
    private Guid _projectA, _taskA1, _taskA2, _taskB;

    public RlsRuntimeRoleTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
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

            var appConn = PostgresConnection.DeriveAppConnectionString(adminConn);
            await PostgresConnection.EnsureAppRoleAsync(appConn, adminConn);

            await using (var seed = new BeaconDbContext(NewAppOptions(appConn)))
            {
                using (TenantScope.EnterUnscoped())
                {
                    var orgA = Org.Create("Runtime A Org");
                    seed.Orgs.Add(orgA);
                    await seed.SaveChangesAsync();
                    var projA = Project.Create("Runtime A Project", null, orgA.Id);
                    seed.Projects.Add(projA);
                    await seed.SaveChangesAsync();
                    var tA1 = TaskItem.Create("Runtime A Task 1", projA.Id, TaskPriority.Medium, TaskType.Task);
                    var tA2 = TaskItem.Create("Runtime A Task 2", projA.Id, TaskPriority.Medium, TaskType.Task);
                    seed.Tasks.Add(tA1);
                    seed.Tasks.Add(tA2);
                    await seed.SaveChangesAsync();

                    var orgB = Org.Create("Runtime B Org");
                    seed.Orgs.Add(orgB);
                    await seed.SaveChangesAsync();
                    var projB = Project.Create("Runtime B Project", null, orgB.Id);
                    seed.Projects.Add(projB);
                    await seed.SaveChangesAsync();
                    var tB = TaskItem.Create("Runtime B Task 1", projB.Id, TaskPriority.Medium, TaskType.Task);
                    seed.Tasks.Add(tB);
                    await seed.SaveChangesAsync();

                    s_projectA = projA.Id;
                    s_taskA1 = tA1.Id;
                    s_taskA2 = tA2.Id;
                    s_taskB = tB.Id;
                }
            }

            s_adminConn = adminConn;
            s_appConn = appConn;
            _initialized = true;
        }

        _adminConn = s_adminConn;
        _appConn = s_appConn;
        _projectA = s_projectA;
        _taskA1 = s_taskA1;
        _taskA2 = s_taskA2;
        _taskB = s_taskB;
    }

    public Task DisposeAsync()
    {
        // Do not drop the DB per test: the isolated DB is created once per process
        // (see _initialized) and a per-test DROP ... WITH (FORCE) terminates
        // server-side backends, leaving dead connections in the Npgsql pool that
        // reset on the next test's first read. The DB is recreated from scratch on
        // the next process run by the DROP at the top of InitializeAsync.
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Rls_RuntimeRole_FreshSession_FailClosed_ViaRawSql()
    {
        if (!_postgres.Available) return;

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"Tasks\"", conn);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync()!);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Rls_RuntimeRole_ProjectScope_HidesOtherProjects_ViaRawSql()
    {
        if (!_postgres.Available) return;

        var ids = await QueryTaskIdsRawAsync(_projectA.ToString(), "false");
        Assert.Contains(_taskA1, ids);
        Assert.Contains(_taskA2, ids);
        Assert.DoesNotContain(_taskB, ids);
    }

    [Fact]
    public async Task Rls_RuntimeRole_Unscoped_SeesAll_ViaRawSql()
    {
        if (!_postgres.Available) return;

        var ids = await QueryTaskIdsRawAsync("", "true");
        Assert.Equal(3, ids.Count);
        Assert.Contains(_taskB, ids);
    }

    [Fact]
    public async Task Ef_RuntimeRole_ProjectScope_IsolatesTasks()
    {
        if (!_postgres.Available) return;

        await using var ctx = new BeaconDbContext(NewAppOptions(_appConn));
        using (TenantScope.EnterProjectScope(_projectA))
        {
            var tasks = await ctx.Tasks.ToListAsync();
            Assert.Equal(2, tasks.Count);
            Assert.DoesNotContain(tasks, t => t.Id == _taskB);
        }
    }

    [Fact]
    public async Task Ef_RuntimeRole_NoScope_FailClosed()
    {
        if (!_postgres.Available) return;

        await using var ctx = new BeaconDbContext(NewAppOptions(_appConn));
        var tasks = await ctx.Tasks.ToListAsync();
        Assert.Empty(tasks);
    }

    private static DbContextOptions<BeaconDbContext> NewAppOptions(string appConn)
    {
        return new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(appConn)
            .AddInterceptors(new TenantRlsConnectionInterceptor())
            .Options;
    }

    private async Task<List<Guid>> QueryTaskIdsRawAsync(string projectGuc, string unscoped)
    {
        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync();
        await using var setCmd = new NpgsqlCommand(
            "SELECT set_config('app.tenant_project_id', @pid, false)," +
            "       set_config('app.tenant_org_id', '', false)," +
            "       set_config('app.tenant_unscoped', @uns, false)", conn);
        setCmd.Parameters.AddWithValue("@pid", projectGuc);
        setCmd.Parameters.AddWithValue("@uns", unscoped);
        await setCmd.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand("SELECT \"Id\" FROM \"Tasks\"", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));
        return ids;
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
