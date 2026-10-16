namespace ProjectBeacon.API.Tests;

using Application.Persistence;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Threading;

/// <summary>
/// P1.5 RLS acceptance: GUC behavior of the TenantRlsConnectionInterceptor on connection
/// open and pool reuse, fail-closed semantics, and the circuit rebind scenario where the
/// scoped ITenantContext has been rebound while the AsyncLocal TenantScope captured at
/// handshake is stale. Runs against a fully-migrated isolated database as the real
/// runtime role (beacon_app), so RLS itself — not the EF query filter — is under test.
/// </summary>
[Collection("postgres-serial")]
public sealed class TenantRebindRlsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string DbName = "beacon_tenant_rebind";

    // xUnit instantiates this class once per test, so the isolated-DB setup
    // (drop/create + MigrateAsync + seed) runs once per process (same pattern
    // as RlsRuntimeRoleTests).
    private static bool _initialized;
    private static string s_adminConn = string.Empty;
    private static string s_appConn = string.Empty;
    private static Guid s_projectA, s_projectB, s_taskA, s_taskB;

    private readonly PostgresFixture _postgres;
    private string _adminConn = string.Empty;
    private string _appConn = string.Empty;
    private Guid _projectA, _projectB, _taskA, _taskB;

    public TenantRebindRlsTests(PostgresFixture postgres) => _postgres = postgres;

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

            var appConn = PostgresConnection.DeriveAppConnectionString(adminConn);
            await PostgresConnection.EnsureAppRoleAsync(appConn, adminConn);

            await using (var seed = new BeaconDbContext(NewAppOptions(appConn)))
            {
                using (TenantScope.EnterUnscoped())
                {
                    var org = Org.Create("Rebind Org");
                    seed.Orgs.Add(org);
                    await seed.SaveChangesAsync();

                    var projA = Project.Create("Rebind A", null, org.Id);
                    var projB = Project.Create("Rebind B", null, org.Id);
                    seed.Projects.Add(projA);
                    seed.Projects.Add(projB);
                    await seed.SaveChangesAsync();

                    var tA = TaskItem.Create("Rebind A Task", projA.Id, TaskPriority.Medium, TaskType.Task);
                    var tB = TaskItem.Create("Rebind B Task", projB.Id, TaskPriority.Medium, TaskType.Task);
                    seed.Tasks.Add(tA);
                    seed.Tasks.Add(tB);
                    await seed.SaveChangesAsync();

                    s_projectA = projA.Id;
                    s_projectB = projB.Id;
                    s_taskA = tA.Id;
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
        _projectB = s_projectB;
        _taskA = s_taskA;
        _taskB = s_taskB;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // The interceptor must copy the current AsyncLocal scope into the GUCs on every
    // connection open, so a circuit connection opened under scope A carries GUC=A.
    [Fact]
    public async Task Interceptor_SetsGucsFromTenantScope_OnConnectionOpen()
    {
        _postgres.RequirePostgres();

        await using var ctx = new BeaconDbContext(NewAppOptions(_appConn));
        using (TenantScope.EnterProjectScope(_projectA))
        {
            await ctx.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            var conn = ctx.Database.GetDbConnection();
            Assert.Equal(_projectA.ToString(), await CurrentGucAsync(conn, "app.tenant_project_id"));
            Assert.Equal("", await CurrentGucAsync(conn, "app.tenant_org_id"));
            Assert.Equal("false", await CurrentGucAsync(conn, "app.tenant_unscoped"));
        }
    }

    // A pooled connection keeps its GUC server-side; the interceptor must re-apply the
    // GUCs on every checkout so a stale value from the previous tenant never leaks.
    [Fact]
    public async Task Interceptor_ReappliesGucs_OnPooledConnectionReuse()
    {
        _postgres.RequirePostgres();

        await using (var ctxA = new BeaconDbContext(NewAppOptions(_appConn)))
        {
            using (TenantScope.EnterProjectScope(_projectA))
            {
                await ctxA.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
                Assert.Equal(_projectA.ToString(),
                    await CurrentGucAsync(ctxA.Database.GetDbConnection(), "app.tenant_project_id"));
            }
        }

        await using (var ctxB = new BeaconDbContext(NewAppOptions(_appConn)))
        {
            using (TenantScope.EnterProjectScope(_projectB))
            {
                await ctxB.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
                Assert.Equal(_projectB.ToString(),
                    await CurrentGucAsync(ctxB.Database.GetDbConnection(), "app.tenant_project_id"));
            }
        }
    }

    // No scope at all: GUCs are empty and the app role is fail-closed (no rows).
    [Fact]
    public async Task NoScope_AppRole_SeesNoRows()
    {
        _postgres.RequirePostgres();

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        // A pooled connection may carry the interceptor's fail-closed GUC (""); a fresh
        // one has the setting unset. Both must be fail-closed (no rows).
        var pid = await CurrentGucAsync(conn, "app.tenant_project_id");
        Assert.True(pid is null || pid == "", $"unexpected GUC value: {pid}");
        Assert.Equal(0, await CountTasksRawAsync(conn));
    }

    // Circuit rebind scenario: the ExecutionContext captured at handshake still carries
    // scope A, while the scoped tenant (what the EF filter reads) has been rebound to B.
    // RLS must follow the scoped tenant: B's own rows stay visible, A's rows stay hidden.
    // Without the reconciliation this test is red (stale GUC=A hides B's rows).
    [Fact]
    public async Task Rebind_ScopedTenant_BeatsStaleHandshakeScope()
    {
        _postgres.RequirePostgres();

        var captured = CaptureScoped(_projectA);

        var tenant = new TenantContext();
        tenant.Assign(_projectB, null, unscoped: false);
        var factory = new BeaconDbFactory(NewAppOptions(_appConn), tenant);

        await RunInContextAsync(captured, async () =>
        {
            await using var db = factory.CreateDbContext();
            Assert.Equal(_projectB, db.FilterProjectId);

            await db.Database.OpenConnectionAsync();
            var conn = db.Database.GetDbConnection();
            Assert.Equal(_projectB.ToString(), await CurrentGucAsync(conn, "app.tenant_project_id"));
            Assert.True(await CountTaskRawAsync(conn, _taskB) == 1,
                "the rebound project's own rows must stay visible to the live circuit");
            Assert.True(await CountTaskRawAsync(conn, _taskA) == 0,
                "the old project's rows must stay hidden under the rebound tenant");
        });
    }

    // RLS policies are keyed by the GUC, not by live membership: after the membership row
    // is deleted, a connection that still carries the old GUC keeps seeing the old
    // project's rows. This documents the exposure window bounded by the 60-second
    // revalidation tick — circuit-side protection comes from the EF scope rebind, not
    // from a membership check inside the RLS policy.
    [Fact]
    public async Task Rls_KeyedByGuc_NotByLiveMembership_StaleGucStillSeesOldProject()
    {
        _postgres.RequirePostgres();

        var user = User.Create("rebind-user-" + Guid.NewGuid().ToString("N")[..8], "rebind-user@example.local", "hash");
        Guid membershipId;
        await using (var admin = new BeaconDbContext(NewAdminOptions(_adminConn)))
        {
            using (TenantScope.EnterUnscoped())
            {
                admin.Users.Add(user);
                var member = ProjectMember.Create(_projectA, user.Id, MemberRole.Member);
                admin.ProjectMembers.Add(member);
                await admin.SaveChangesAsync(TestContext.Current.CancellationToken);
                membershipId = member.Id;
            }
        }

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetGucAsync(conn, _projectA.ToString(), "false");
        Assert.True(await MembershipExistsAsync(user.Id, _projectA));
        Assert.True(await CountTaskRawAsync(conn, _taskA) == 1,
            "with live membership and GUC=A the old project is visible");

        await using (var admin = new BeaconDbContext(NewAdminOptions(_adminConn)))
        {
            using (TenantScope.EnterUnscoped())
            {
                var member = await admin.ProjectMembers
                    .IgnoreQueryFilters()
                    .SingleAsync(m => m.Id == membershipId, TestContext.Current.CancellationToken);
                admin.ProjectMembers.Remove(member);
                await admin.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }
        Assert.False(await MembershipExistsAsync(user.Id, _projectA));

        // Same connection, same stale GUC: the revoked project is still visible to RLS.
        Assert.True(await CountTaskRawAsync(conn, _taskA) == 1,
            "RLS is keyed by the GUC, not live membership (stale GUC keeps the old project visible)");
    }

    // The unscoped GUC (bootstrap/migration/worker path) sees every row.
    [Fact]
    public async Task UnscopedGuc_SeesAllRows()
    {
        _postgres.RequirePostgres();

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetGucAsync(conn, "", "true");
        Assert.Equal(2, await CountTasksRawAsync(conn));
    }

    private static ExecutionContext CaptureScoped(Guid projectId)
    {
        using (TenantScope.EnterProjectScope(projectId))
            return ExecutionContext.Capture()!;
    }

    private static Task RunInContextAsync(ExecutionContext context, Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionContext.Run(context, _ =>
        {
            try
            {
                var task = action();
                _ = task.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        tcs.SetException(t.Exception!.InnerException!);
                    else if (t.IsCanceled)
                        tcs.SetCanceled();
                    else
                        tcs.SetResult();
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    private async Task<bool> MembershipExistsAsync(Guid userId, Guid projectId)
    {
        await using var admin = new BeaconDbContext(NewAdminOptions(_adminConn));
        using (TenantScope.EnterUnscoped())
        {
            return await admin.ProjectMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.UserId == userId && m.ProjectId == projectId);
        }
    }

    private static async Task<string?> CurrentGucAsync(System.Data.Common.DbConnection conn, string setting)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT current_setting('{setting}', true)";
        var value = await cmd.ExecuteScalarAsync();
        return value as string;
    }

    private static async Task SetGucAsync(NpgsqlConnection conn, string projectGuc, string unscoped)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT set_config('app.tenant_project_id', @pid, false)," +
            "       set_config('app.tenant_org_id', '', false)," +
            "       set_config('app.tenant_unscoped', @uns, false)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@pid", projectGuc));
        cmd.Parameters.Add(new NpgsqlParameter("@uns", unscoped));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountTaskRawAsync(System.Data.Common.DbConnection conn, Guid taskId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM \"Tasks\" WHERE \"Id\" = @tid";
        var p = cmd.CreateParameter();
        p.ParameterName = "@tid";
        p.Value = taskId;
        cmd.Parameters.Add(p);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()!);
    }

    private static async Task<int> CountTasksRawAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"Tasks\"", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()!);
    }

    private static DbContextOptions<BeaconDbContext> NewAppOptions(string appConn)
    {
        return new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(appConn)
            .AddInterceptors(new TenantRlsConnectionInterceptor())
            .Options;
    }

    private static DbContextOptions<BeaconDbContext> NewAdminOptions(string adminConn)
    {
        return new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(adminConn)
            .AddInterceptors(new TenantRlsConnectionInterceptor())
            .Options;
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
