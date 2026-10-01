namespace ProjectBeacon.API.Tests;

using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

[Collection("postgres-serial")]
public sealed class RlsIsolationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly string _rlsRole = "rls_" + Guid.NewGuid().ToString("N");
    private readonly PostgresFixture _postgres;
    private bool _roleCreated;
    private bool _rlsApplied;

    private static readonly string[] ProjectScopedTables =
    {
        "Tasks", "Labels", "LabelPaths", "Reports", "Milestones", "Constraints",
        "Decisions", "ApiTokens", "PipelineSessions", "ProjectRuntimes",
        "ProjectMembers", "ProjectInvites", "ReviewVerdicts", "Subtasks",
        "RoleBindings", "TaskPhases", "TaskSteps", "ChatSessions", "ChatParts",
        "EvalRuns", "ReviewRuns", "ContextSections", "ContextRevisions"
    };

    private static readonly string[] OrgScopedTables = { "Projects", "OrgMembers", "OrgInvites" };

    public RlsIsolationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();

        await Exec(conn, $"CREATE ROLE {_rlsRole} LOGIN PASSWORD 'rls_test'");
        _roleCreated = true;
        await Exec(conn, $"GRANT {_rlsRole} TO CURRENT_USER");
        await Exec(conn, $"GRANT USAGE ON SCHEMA public TO {_rlsRole}");
        await Exec(conn, $"GRANT SELECT ON ALL TABLES IN SCHEMA public TO {_rlsRole}");

        foreach (var table in ProjectScopedTables)
        {
            await Exec(conn, $"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY");
            await Exec(conn, $"ALTER TABLE \"{table}\" FORCE ROW LEVEL SECURITY");
            await Exec(conn, $"DROP POLICY IF EXISTS tenant_project_isolation ON \"{table}\"");
            await Exec(conn,
                $"CREATE POLICY tenant_project_isolation ON \"{table}\" " +
                "USING (current_setting('app.tenant_unscoped', true) = 'true' " +
                "OR \"ProjectId\"::text = current_setting('app.tenant_project_id', true))");
        }

        foreach (var table in OrgScopedTables)
        {
            await Exec(conn, $"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY");
            await Exec(conn, $"ALTER TABLE \"{table}\" FORCE ROW LEVEL SECURITY");
            await Exec(conn, $"DROP POLICY IF EXISTS tenant_org_isolation ON \"{table}\"");
            await Exec(conn,
                $"CREATE POLICY tenant_org_isolation ON \"{table}\" " +
                "USING (current_setting('app.tenant_unscoped', true) = 'true' " +
                "OR \"OrgId\"::text = current_setting('app.tenant_org_id', true))");
        }

        _rlsApplied = true;
    }

    public async Task DisposeAsync()
    {
        if (!_roleCreated) return;

        await using var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();

        if (_rlsApplied)
        {
            foreach (var table in ProjectScopedTables)
            {
                await Exec(conn, $"DROP POLICY IF EXISTS tenant_project_isolation ON \"{table}\"");
                await Exec(conn, $"ALTER TABLE \"{table}\" NO FORCE ROW LEVEL SECURITY");
                await Exec(conn, $"ALTER TABLE \"{table}\" DISABLE ROW LEVEL SECURITY");
            }

            foreach (var table in OrgScopedTables)
            {
                await Exec(conn, $"DROP POLICY IF EXISTS tenant_org_isolation ON \"{table}\"");
                await Exec(conn, $"ALTER TABLE \"{table}\" NO FORCE ROW LEVEL SECURITY");
                await Exec(conn, $"ALTER TABLE \"{table}\" DISABLE ROW LEVEL SECURITY");
            }
        }

        await Exec(conn, $"REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM {_rlsRole}");
        await Exec(conn, $"REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM {_rlsRole}");
        await Exec(conn, $"REVOKE USAGE ON SCHEMA public FROM {_rlsRole}");
        await Exec(conn, $"REVOKE {_rlsRole} FROM CURRENT_USER");
        await Exec(conn, $"DROP ROLE IF EXISTS {_rlsRole}");
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid ProjectId, Guid TaskId)> SeedTestDataAsync(string suffix)
    {
        await using var seed = _postgres.CreateContext();
        using (TenantScope.EnterUnscoped())
        {
            var org = Org.Create($"Rls {suffix} Org");
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync();

            var proj = Project.Create($"Rls {suffix} Project", null, org.Id);
            seed.Projects.Add(proj);
            await seed.SaveChangesAsync();

            var task = TaskItem.Create($"Rls {suffix} Task", proj.Id, TaskPriority.Medium, TaskType.Task);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync();

            return (proj.Id, task.Id);
        }
    }

    private async Task<NpgsqlConnection> OpenRlsConnectionAsync()
    {
        var conn = new NpgsqlConnection(_postgres.ConnectionString);
        await conn.OpenAsync();
        await Exec(conn, $"SET ROLE {_rlsRole}");
        return conn;
    }

    [Fact]
    public async Task Rls_ProjectScope_HidesOtherProjectRows_ViaRawSql()
    {
        var (projectA, taskA) = await SeedTestDataAsync("A");
        var (projectB, taskB) = await SeedTestDataAsync("B");

        await using var conn = await OpenRlsConnectionAsync();
        await using (var setCmd = new NpgsqlCommand(
            "SELECT set_config('app.tenant_project_id', @pid, false)," +
            "       set_config('app.tenant_unscoped', 'false', false)", conn))
        {
            setCmd.Parameters.AddWithValue("@pid", projectA.ToString());
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand("SELECT \"Id\" FROM \"Tasks\"", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));

        Assert.Contains(taskA, ids);
        Assert.DoesNotContain(taskB, ids);
    }

    [Fact]
    public async Task Rls_NoScope_DeniesAllRows_ViaRawSql()
    {
        var (_, taskA) = await SeedTestDataAsync("FailClosed");

        await using var conn = await OpenRlsConnectionAsync();
        await Exec(conn,
            "SELECT set_config('app.tenant_project_id', '', false)," +
            "       set_config('app.tenant_org_id', '', false)," +
            "       set_config('app.tenant_unscoped', 'false', false)");

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"Tasks\" WHERE \"Id\" = @tid", conn);
        cmd.Parameters.AddWithValue("@tid", taskA);
        var count = (long)(await cmd.ExecuteScalarAsync()!);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Rls_Unscoped_AllowsAllRows_ViaRawSql()
    {
        var (_, taskA) = await SeedTestDataAsync("Unscoped");

        await using var conn = await OpenRlsConnectionAsync();
        await Exec(conn,
            "SELECT set_config('app.tenant_project_id', '', false)," +
            "       set_config('app.tenant_org_id', '', false)," +
            "       set_config('app.tenant_unscoped', 'true', false)");

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"Tasks\" WHERE \"Id\" = @tid", conn);
        cmd.Parameters.AddWithValue("@tid", taskA);
        var count = (long)(await cmd.ExecuteScalarAsync()!);
        Assert.Equal(1, count);
    }
}
