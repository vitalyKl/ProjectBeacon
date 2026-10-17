namespace ProjectBeacon.API.Tests;

using Domain.Entities.Context;
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
    private static Guid s_projectA, s_projectB, s_taskA1, s_taskA2, s_taskB;
    private static Guid s_user, s_commentA, s_commentB, s_depOwn, s_depCross, s_decisionA, s_decisionB;

    private readonly PostgresFixture _postgres;
    private string _adminConn = string.Empty;
    private string _appConn = string.Empty;
    private Guid _projectA, _projectB, _taskA1, _taskA2, _taskB;
    private Guid _user, _commentA, _commentB, _depOwn, _depCross, _decisionA, _decisionB;

    public RlsRuntimeRoleTests(PostgresFixture postgres) => _postgres = postgres;

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

                    // Child-table rows: comments, dependency pairs (within-tenant and
                    // cross-tenant) and decision-task links, one set per tenant.
                    var user = User.Create("rls-runtime-user", "rls-runtime-user@example.test", "rls-test-password-hash");
                    seed.Users.Add(user);
                    await seed.SaveChangesAsync();

                    var commentA = TaskComment.Create("own-tenant comment", tA1.Id, user.Id);
                    var commentB = TaskComment.Create("foreign-tenant comment", tB.Id, user.Id);
                    seed.TaskComments.Add(commentA);
                    seed.TaskComments.Add(commentB);
                    await seed.SaveChangesAsync();

                    var depOwn = TaskDependency.Create(tA1.Id, tA2.Id); // both tasks in project A
                    var depCross = TaskDependency.Create(tA1.Id, tB.Id); // dependent task in project B
                    seed.TaskDependencies.Add(depOwn);
                    seed.TaskDependencies.Add(depCross);
                    await seed.SaveChangesAsync();

                    var decisionA = Decision.Create("Runtime A Decision", "ctx", "body", projA.Id);
                    var decisionB = Decision.Create("Runtime B Decision", "ctx", "body", projB.Id);
                    seed.Decisions.Add(decisionA);
                    seed.Decisions.Add(decisionB);
                    await seed.SaveChangesAsync();

                    seed.DecisionTasks.Add(DecisionTask.Create(decisionA.Id, tA1.Id));
                    seed.DecisionTasks.Add(DecisionTask.Create(decisionB.Id, tB.Id));
                    await seed.SaveChangesAsync();

                    s_projectA = projA.Id;
                    s_projectB = projB.Id;
                    s_taskA1 = tA1.Id;
                    s_taskA2 = tA2.Id;
                    s_taskB = tB.Id;
                    s_user = user.Id;
                    s_commentA = commentA.Id;
                    s_commentB = commentB.Id;
                    s_depOwn = depOwn.Id;
                    s_depCross = depCross.Id;
                    s_decisionA = decisionA.Id;
                    s_decisionB = decisionB.Id;
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
        _taskA1 = s_taskA1;
        _taskA2 = s_taskA2;
        _taskB = s_taskB;
        _user = s_user;
        _commentA = s_commentA;
        _commentB = s_commentB;
        _depOwn = s_depOwn;
        _depCross = s_depCross;
        _decisionA = s_decisionA;
        _decisionB = s_decisionB;
    }

    public ValueTask DisposeAsync()
    {
        // Do not drop the DB per test: the isolated DB is created once per process
        // (see _initialized) and a per-test DROP ... WITH (FORCE) terminates
        // server-side backends, leaving dead connections in the Npgsql pool that
        // reset on the next test's first read. The DB is recreated from scratch on
        // the next process run by the DROP at the top of InitializeAsync.
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Rls_RuntimeRole_FreshSession_FailClosed_ViaRawSql()
    {
        _postgres.RequirePostgres();

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"Tasks\"", conn);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken: TestContext.Current.CancellationToken)!);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Rls_RuntimeRole_ProjectScope_HidesOtherProjects_ViaRawSql()
    {
        _postgres.RequirePostgres();

        var ids = await QueryTaskIdsRawAsync(_projectA.ToString(), "false");
        Assert.Contains(_taskA1, ids);
        Assert.Contains(_taskA2, ids);
        Assert.DoesNotContain(_taskB, ids);
    }

    [Fact]
    public async Task Rls_RuntimeRole_Unscoped_SeesAll_ViaRawSql()
    {
        _postgres.RequirePostgres();

        var ids = await QueryTaskIdsRawAsync("", "true");
        Assert.Equal(3, ids.Count);
        Assert.Contains(_taskB, ids);
    }

    [Fact]
    public async Task Ef_RuntimeRole_ProjectScope_IsolatesTasks()
    {
        _postgres.RequirePostgres();

        await using var ctx = new BeaconDbContext(NewAppOptions(_appConn));
        using (TenantScope.EnterProjectScope(_projectA))
        {
            var tasks = await ctx.Tasks.ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, tasks.Count);
            Assert.DoesNotContain(tasks, t => t.Id == _taskB);
        }
    }

    [Fact]
    public async Task Ef_RuntimeRole_NoScope_FailClosed()
    {
        _postgres.RequirePostgres();

        await using var ctx = new BeaconDbContext(NewAppOptions(_appConn));
        var tasks = await ctx.Tasks.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(tasks);
    }

    // Cross-tenant DML on the real migrated schema as the real runtime role:
    // RLS WITH CHECK rejects foreign inserts; foreign UPDATE/DELETE affect zero rows.
    [Fact]
    public async Task Rls_RuntimeRole_CrossTenantInsert_IsRejected_ViaRawSql()
    {
        _postgres.RequirePostgres();

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetTenantScopeAsync(conn, _projectA.ToString(), "false");

        var foreignId = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertTaskRawAsync(conn, foreignId, _projectB));
        Assert.Equal("42501", ex.SqlState); // row_security_violation: WITH CHECK rejected the row

        Assert.Equal(0, await CountTaskByIdRawAsync(conn, foreignId));
    }

    [Fact]
    public async Task Rls_RuntimeRole_CrossTenantUpdateDelete_AffectZeroRows_ViaRawSql()
    {
        _postgres.RequirePostgres();

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetTenantScopeAsync(conn, _projectA.ToString(), "false");

        Assert.Equal(0, await UpdateTaskTitleRawAsync(conn, _taskB, "rls cross-tenant pwned"));
        Assert.Equal(0, await DeleteTaskRawAsync(conn, _taskB));

        await using var unscoped = new NpgsqlConnection(_appConn);
        await unscoped.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetTenantScopeAsync(unscoped, "", "true");
        Assert.Equal(1, await CountTaskByIdRawAsync(unscoped, _taskB));
        Assert.Equal("Runtime B Task 1", await TaskTitleRawAsync(unscoped, _taskB));
    }

    [Fact]
    public async Task Rls_RuntimeRole_OwnTenantInsertUpdateDelete_Work_ViaRawSql()
    {
        _postgres.RequirePostgres();

        var ownId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetTenantScopeAsync(conn, _projectA.ToString(), "false");

        await InsertTaskRawAsync(conn, ownId, _projectA);
        Assert.Equal(1, await CountTaskByIdRawAsync(conn, ownId));

        Assert.Equal(1, await UpdateTaskTitleRawAsync(conn, ownId, "own-tenant update"));
        Assert.Equal("own-tenant update", await TaskTitleRawAsync(conn, ownId));

        Assert.Equal(1, await DeleteTaskRawAsync(conn, ownId));

        await using var unscoped = new NpgsqlConnection(_appConn);
        await unscoped.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetTenantScopeAsync(unscoped, "", "true");
        Assert.Equal(0, await CountTaskByIdRawAsync(unscoped, ownId));
    }

    // Child-table RLS (TaskComments / TaskDependencies / DecisionTables): these tables
    // carry no tenant column of their own; their visibility derives from the parent row
    // through the parent policy applied inside the EXISTS subquery. Verified behaviorally
    // as the real runtime role on the real migrated schema — not by inspecting policy text.
    [Fact]
    public async Task Rls_RuntimeRole_ChildTables_CrossTenantSelect_Hidden_ViaRawSql()
    {
        _postgres.RequirePostgres();

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetTenantScopeAsync(conn, _projectA.ToString(), "false");

        // Comments: the own-tenant comment is visible; the comment on a foreign task is not.
        Assert.Equal(1, await CountTableRowsAsync(conn, "TaskComments"));
        Assert.Equal(1, await CountRowByIdAsync(conn, "TaskComments", _commentA));
        Assert.Equal(0, await CountRowByIdAsync(conn, "TaskComments", _commentB));

        // Dependencies: the within-tenant pair is visible; the cross-tenant pair is hidden
        // because the dependent task is out of scope.
        Assert.Equal(1, await CountTableRowsAsync(conn, "TaskDependencies"));
        Assert.Equal(1, await CountRowByIdAsync(conn, "TaskDependencies", _depOwn));
        Assert.Equal(0, await CountRowByIdAsync(conn, "TaskDependencies", _depCross));

        // Decision-task links: the own link is visible; the foreign link is not.
        Assert.Equal(1, await CountDecisionTaskRowsAsync(conn));
        Assert.Equal(1, await CountDecisionTaskAsync(conn, _decisionA, _taskA1));
        Assert.Equal(0, await CountDecisionTaskAsync(conn, _decisionB, _taskB));
    }

    [Fact]
    public async Task Rls_RuntimeRole_ChildTables_Unscoped_SeesAll_ViaRawSql()
    {
        _postgres.RequirePostgres();

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetTenantScopeAsync(conn, "", "true");

        Assert.Equal(2, await CountTableRowsAsync(conn, "TaskComments"));
        Assert.Equal(2, await CountTableRowsAsync(conn, "TaskDependencies"));
        Assert.Equal(2, await CountDecisionTaskRowsAsync(conn));
    }

    // Cross-tenant DML on child tables: WITH CHECK rejects inserts that link to an
    // out-of-scope parent row.
    [Fact]
    public async Task Rls_RuntimeRole_ChildTables_CrossTenantInsert_IsRejected_ViaRawSql()
    {
        _postgres.RequirePostgres();

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetTenantScopeAsync(conn, _projectA.ToString(), "false");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertCommentAsync(conn, Guid.NewGuid(), _taskB, _user));
        Assert.Equal("42501", ex.SqlState); // row_security_violation: foreign parent task

        var ex2 = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertDependencyAsync(conn, Guid.NewGuid(), _taskA2, _taskB));
        Assert.Equal("42501", ex2.SqlState); // dependent task is outside the tenant

        var ex3 = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertDecisionTaskAsync(conn, _decisionA, _taskB));
        Assert.Equal("42501", ex3.SqlState); // linked task is outside the tenant
    }

    [Fact]
    public async Task Rls_RuntimeRole_ChildTables_CrossTenantUpdateDelete_AffectZeroRows_ViaRawSql()
    {
        _postgres.RequirePostgres();

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetTenantScopeAsync(conn, _projectA.ToString(), "false");

        Assert.Equal(0, await UpdateCommentContentAsync(conn, _commentB, "rls cross-tenant pwned"));
        Assert.Equal(0, await DeleteRowByIdAsync(conn, "TaskDependencies", _depCross));
        Assert.Equal(0, await DeleteDecisionTaskAsync(conn, _decisionB, _taskB));

        await using var unscoped = new NpgsqlConnection(_appConn);
        await unscoped.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await SetTenantScopeAsync(unscoped, "", "true");
        Assert.Equal("foreign-tenant comment", await CommentContentAsync(unscoped, _commentB));
        Assert.Equal(1, await CountRowByIdAsync(unscoped, "TaskDependencies", _depCross));
        Assert.Equal(1, await CountDecisionTaskAsync(unscoped, _decisionB, _taskB));
    }

    // Pool-leak: sequential connections must reuse the same physical backend (asserted via
    // pg_backend_pid), and the tenant GUCs set on one must not survive to the next scope.
    [Fact]
    public async Task Rls_PooledConnection_TenantScope_DoesNotLeak_AcrossSequentialScopes()
    {
        _postgres.RequirePostgres();

        // Drain the process-wide pool for this connection string: earlier tests in this
        // class leave idle backends in it, and the pool does not guarantee which idle
        // connection it hands out next, so the same-backend assertions below would be
        // order-dependent without a clean start.
        NpgsqlConnection.ClearPool(new NpgsqlConnection(_appConn));

        long backendPid;
        await using (var connA = new NpgsqlConnection(_appConn))
        {
            await connA.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
            backendPid = await BackendPidAsync(connA);
            await SetTenantScopeAsync(connA, _projectA.ToString(), "false");
            Assert.Equal(2, await CountTasksRawAsync(connA));
        }

        await using (var connB = new NpgsqlConnection(_appConn))
        {
            await connB.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(backendPid, await BackendPidAsync(connB));
            await SetTenantScopeAsync(connB, _projectB.ToString(), "false");
            Assert.Equal(1, await CountTasksRawAsync(connB));
            Assert.Equal(0, await CountTaskByIdRawAsync(connB, _taskA1));
            Assert.Equal(1, await CountTaskByIdRawAsync(connB, _taskB));
        }

        // No scope applied at all on the reused backend: fail closed, no stale A or B scope.
        await using (var connC = new NpgsqlConnection(_appConn))
        {
            await connC.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(backendPid, await BackendPidAsync(connC));
            Assert.Equal(0, await CountTasksRawAsync(connC));
        }
    }

    // The production EF path (TenantRlsConnectionInterceptor on every connection open) must
    // isolate sequential scopes even when the pool hands back the same physical connection.
    [Fact]
    public async Task Ef_SequentialScopes_OnPooledConnection_AreIsolated()
    {
        _postgres.RequirePostgres();

        await using (var ctxA = new BeaconDbContext(NewAppOptions(_appConn)))
        {
            using (TenantScope.EnterProjectScope(_projectA))
            {
                var tasks = await ctxA.Tasks.ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, tasks.Count);
                Assert.DoesNotContain(tasks, t => t.Id == _taskB);
            }
        }

        await using (var ctxB = new BeaconDbContext(NewAppOptions(_appConn)))
        {
            using (TenantScope.EnterProjectScope(_projectB))
            {
                var tasks = await ctxB.Tasks.ToListAsync(TestContext.Current.CancellationToken);
                Assert.Single(tasks);
                Assert.Equal(_taskB, tasks[0].Id);
            }
        }

        await using (var ctxC = new BeaconDbContext(NewAppOptions(_appConn)))
        {
            var tasks = await ctxC.Tasks.ToListAsync(TestContext.Current.CancellationToken);
            Assert.Empty(tasks);
        }
    }

    // Drift guard: every table in public that is tenant-scoped — directly through a
    // ProjectId/OrgId column, or transitively through a foreign key to such a table —
    // must be FORCE-RLS protected by a permissive tenant policy whose USING predicate
    // references the app.tenant_* GUCs. with_check is accepted as NULL: Postgres does not
    // copy USING into the catalog for DML, it reuses the USING expression at plan time
    // (verified behaviorally by the INSERT/UPDATE/DELETE tests below). All checks go
    // through the system catalog (pg_class / pg_attribute / pg_constraint / pg_policies),
    // not policy text. The allowlist is the closed set of intentionally non-tenant
    // tables; a new table that is neither scoped nor allowlisted fails this test and
    // forces a decision.
    [Fact]
    public async Task Migration_RlsPolicy_CoversEveryTenantScopedTable()
    {
        _postgres.RequirePostgres();

        // Intentionally not tenant-scoped: identity, user-owned rows, and global catalogs.
        var allowlist = new HashSet<string>
        {
            "__EFMigrationsHistory", // EF migration bookkeeping
            "Users",                 // global identity
            "Sessions",             // user auth sessions
            "OpenCodeConnections",  // user-owned agent connections
            "Orgs",                 // tenant root; org scope is carried by members/invites
            "PasswordResetTokens",  // user credential recovery
            "LocalModelBackends",   // user-owned model registry
            "AgentTemplates",       // user-owned agent templates
            "TaskKinds",            // global catalog
            "TaskKindPhases",       // catalog child of TaskKinds
            "DaemonDevices",        // user-owned devices, not tenant-filtered
            "DeviceHostSamples"     // device-scoped host metrics
        };

        // Carries a ProjectId column but is device-scoped: the ProjectId is optional
        // context, not a tenant scope (see the entity's doc comment).
        var exemptDirect = new HashSet<string> { "WorkstationCommands" };

        await using var conn = new NpgsqlConnection(_adminConn);
        await conn.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);

        var allTables = await ReadTableListAsync(conn,
            "SELECT c.relname " +
            "FROM pg_class c " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'public' AND c.relkind = 'r'");

        var directTables = await ReadTableListAsync(conn,
            "SELECT DISTINCT c.relname " +
            "FROM pg_class c " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND a.attname IN ('ProjectId','OrgId') " +
            "WHERE n.nspname = 'public' AND c.relkind = 'r'");

        var childTables = new List<string>();
        if (directTables.Count > 0)
        {
            await using var fk = new NpgsqlCommand(
                "SELECT DISTINCT tc.relname " +
                "FROM pg_class tc " +
                "JOIN pg_namespace tn ON tn.oid = tc.relnamespace " +
                "JOIN pg_constraint con ON con.conrelid = tc.oid AND con.contype = 'f' " +
                "JOIN pg_class rc ON rc.oid = con.confrelid " +
                "WHERE tn.nspname = 'public' AND tc.relkind = 'r' AND rc.relname = ANY(@direct)", conn);
            fk.Parameters.Add(new NpgsqlParameter("@direct", directTables.ToArray()));
            using var reader = await fk.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken))
                childTables.Add(reader.GetString(0));
        }

        var directSet = new HashSet<string>(directTables);
        var childSet = new HashSet<string>(childTables);

        Assert.True(directTables.Count > 0, "sanity: expected at least one table with a tenant column");
        Assert.True(
            childSet.Contains("TaskComments") && childSet.Contains("TaskDependencies") && childSet.Contains("DecisionTasks"),
            "sanity: expected the FK-scoped child tables to be discovered");

        var violations = new List<string>();
        foreach (var table in allTables)
        {
            if (allowlist.Contains(table))
            {
                // A stale allowlist entry would silently hide a table that drifted back
                // into tenant scope.
                if (directSet.Contains(table) || childSet.Contains(table))
                    violations.Add($"{table}: allow-listed but still tenant-scoped");
                continue;
            }

            if (exemptDirect.Contains(table))
            {
                if (!directSet.Contains(table))
                    violations.Add($"{table}: exempt but no longer has a tenant column");
                continue;
            }

            if (!directSet.Contains(table) && !childSet.Contains(table))
            {
                violations.Add($"{table}: not tenant-scoped and not allow-listed; add RLS or a justified allowlist entry");
                continue;
            }

            await using var check = new NpgsqlCommand(
                "SELECT c.relrowsecurity, c.relforcerowsecurity, " +
                "EXISTS (SELECT 1 FROM pg_policies p " +
                "         WHERE p.schemaname = 'public' AND p.tablename = @t " +
                "         AND p.permissive = 'PERMISSIVE' " +
                "         AND p.qual LIKE '%app.tenant_%' " +
                "         AND (p.with_check IS NULL OR p.with_check LIKE '%app.tenant_%')) " +
                "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE n.nspname = 'public' AND c.relname = @t", conn);
            check.Parameters.Add(new NpgsqlParameter("@t", table));
            using var reader = await check.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            if (!await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken))
            {
                violations.Add($"{table}: missing from catalog");
                continue;
            }
            if (!reader.GetBoolean(0))
                violations.Add($"{table}: relrowsecurity=false");
            if (!reader.GetBoolean(1))
                violations.Add($"{table}: relforcerowsecurity=false");
            if (!reader.GetBoolean(2))
                violations.Add($"{table}: no permissive tenant RLS policy with tenant USING and WITH CHECK predicates");
        }

        Assert.True(violations.Count == 0, string.Join("; ", violations));
    }

    private static async Task SetTenantScopeAsync(NpgsqlConnection conn, string projectGuc, string unscoped)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT set_config('app.tenant_project_id', @pid, false)," +
            "       set_config('app.tenant_org_id', '', false)," +
            "       set_config('app.tenant_unscoped', @uns, false)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@pid", projectGuc));
        cmd.Parameters.Add(new NpgsqlParameter("@uns", unscoped));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> BackendPidAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_backend_pid()", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()!);
    }

    private static async Task<int> CountTasksRawAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"Tasks\"", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()!);
    }

    private static async Task<int> CountTaskByIdRawAsync(NpgsqlConnection conn, Guid taskId)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"Tasks\" WHERE \"Id\" = @tid", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@tid", taskId));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()!);
    }

    private static async Task<string?> TaskTitleRawAsync(NpgsqlConnection conn, Guid taskId)
    {
        await using var cmd = new NpgsqlCommand("SELECT \"Title\" FROM \"Tasks\" WHERE \"Id\" = @id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", taskId));
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private static async Task InsertTaskRawAsync(NpgsqlConnection conn, Guid taskId, Guid projectId)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO \"Tasks\" (\"Id\", \"ProjectId\", \"Title\", \"Status\", \"Priority\", \"Type\", \"SubStage\", \"CreatedAt\") " +
            "VALUES (@id, @pid, @title, 'Todo', 'Medium', 'Task', 'RequirementGathering', @created)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", taskId));
        cmd.Parameters.Add(new NpgsqlParameter("@pid", projectId));
        cmd.Parameters.Add(new NpgsqlParameter("@title", "Rls dml probe"));
        cmd.Parameters.Add(new NpgsqlParameter("@created", DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> UpdateTaskTitleRawAsync(NpgsqlConnection conn, Guid taskId, string title)
    {
        await using var cmd = new NpgsqlCommand("UPDATE \"Tasks\" SET \"Title\" = @title WHERE \"Id\" = @id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@title", title));
        cmd.Parameters.Add(new NpgsqlParameter("@id", taskId));
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> DeleteTaskRawAsync(NpgsqlConnection conn, Guid taskId)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM \"Tasks\" WHERE \"Id\" = @id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", taskId));
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadTableListAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var tables = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken))
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<int> CountTableRowsAsync(NpgsqlConnection conn, string table)
    {
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()!);
    }

    private static async Task<int> CountRowByIdAsync(NpgsqlConnection conn, string table, Guid id)
    {
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\" WHERE \"Id\" = @id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", id));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()!);
    }

    private static async Task<int> DeleteRowByIdAsync(NpgsqlConnection conn, string table, Guid id)
    {
        await using var cmd = new NpgsqlCommand($"DELETE FROM \"{table}\" WHERE \"Id\" = @id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", id));
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountDecisionTaskRowsAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"DecisionTasks\"", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()!);
    }

    private static async Task<int> CountDecisionTaskAsync(NpgsqlConnection conn, Guid decisionId, Guid taskId)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM \"DecisionTasks\" WHERE \"DecisionId\" = @did AND \"TaskId\" = @tid", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@did", decisionId));
        cmd.Parameters.Add(new NpgsqlParameter("@tid", taskId));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()!);
    }

    private static async Task InsertCommentAsync(NpgsqlConnection conn, Guid commentId, Guid taskId, Guid userId)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO \"TaskComments\" (\"Id\", \"TaskId\", \"UserId\", \"Content\", \"CreatedAt\") " +
            "VALUES (@id, @tid, @uid, 'rls dml probe', @created)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", commentId));
        cmd.Parameters.Add(new NpgsqlParameter("@tid", taskId));
        cmd.Parameters.Add(new NpgsqlParameter("@uid", userId));
        cmd.Parameters.Add(new NpgsqlParameter("@created", DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> UpdateCommentContentAsync(NpgsqlConnection conn, Guid commentId, string content)
    {
        await using var cmd = new NpgsqlCommand("UPDATE \"TaskComments\" SET \"Content\" = @content WHERE \"Id\" = @id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@content", content));
        cmd.Parameters.Add(new NpgsqlParameter("@id", commentId));
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> CommentContentAsync(NpgsqlConnection conn, Guid commentId)
    {
        await using var cmd = new NpgsqlCommand("SELECT \"Content\" FROM \"TaskComments\" WHERE \"Id\" = @id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", commentId));
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private static async Task InsertDependencyAsync(NpgsqlConnection conn, Guid depId, Guid taskId, Guid dependentTaskId)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO \"TaskDependencies\" (\"Id\", \"TaskId\", \"DependentTaskId\", \"CreatedAt\") " +
            "VALUES (@id, @tid, @dtid, @created)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@id", depId));
        cmd.Parameters.Add(new NpgsqlParameter("@tid", taskId));
        cmd.Parameters.Add(new NpgsqlParameter("@dtid", dependentTaskId));
        cmd.Parameters.Add(new NpgsqlParameter("@created", DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertDecisionTaskAsync(NpgsqlConnection conn, Guid decisionId, Guid taskId)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO \"DecisionTasks\" (\"DecisionId\", \"TaskId\") VALUES (@did, @tid)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@did", decisionId));
        cmd.Parameters.Add(new NpgsqlParameter("@tid", taskId));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> DeleteDecisionTaskAsync(NpgsqlConnection conn, Guid decisionId, Guid taskId)
    {
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM \"DecisionTasks\" WHERE \"DecisionId\" = @did AND \"TaskId\" = @tid", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@did", decisionId));
        cmd.Parameters.Add(new NpgsqlParameter("@tid", taskId));
        return await cmd.ExecuteNonQueryAsync();
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
