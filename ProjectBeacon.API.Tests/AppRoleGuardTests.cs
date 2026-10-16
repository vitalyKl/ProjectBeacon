namespace ProjectBeacon.API.Tests;

using Infrastructure.Data;
using Npgsql;

/// <summary>
/// Fail-closed role guard against a live PostgreSQL instance: the guard must read the actual
/// connected role via current_user and report SUPERUSER, BYPASSRLS, same-login, and
/// unreachable-server scenarios as unsafe without leaking connection details.
/// </summary>
[Collection("postgres-serial")]
public sealed class AppRoleGuardTests : IClassFixture<PostgresFixture>
{
    private const string ProbePassword = "guard-probe-password";
    private const string LeakMarker = "LeakMarker_9f3k2";

    private readonly PostgresFixture _postgres;

    public AppRoleGuardTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Guard_AppRole_NonSuperuser_IsVerifiedSafe()
    {
        _postgres.RequirePostgres();

        var status = await PostgresConnection.GetAppRoleStatusAsync(_postgres.AppConnectionString);

        Assert.True(status.Verified);
        Assert.Equal(PostgresConnection.AppRoleName, status.RoleName);
        Assert.False(status.Superuser);
        Assert.False(status.BypassRls);
        Assert.False(PostgresConnection.IsUnsafe(status));
    }

    [Fact]
    public async Task Guard_SuperuserRole_IsReportedUnsafe()
    {
        _postgres.RequirePostgres();

        var role = $"guard_super_{Guid.NewGuid():N}";
        await using (var admin = await OpenAdminAsync())
            await CreateRoleAsync(admin, role, "SUPERUSER");

        var status = await PostgresConnection.GetAppRoleStatusAsync(
            WithPassword(WithUsername(_postgres.ConnectionString, role), ProbePassword));

        Assert.True(status.Verified);
        Assert.Equal(role, status.RoleName);
        Assert.True(status.Superuser);
        Assert.True(PostgresConnection.IsUnsafe(status));

        await DropRoleAsync(role);
    }

    [Fact]
    public async Task Guard_BypassRlsRole_IsReportedUnsafe()
    {
        _postgres.RequirePostgres();

        var role = $"guard_bypass_{Guid.NewGuid():N}";
        await using (var admin = await OpenAdminAsync())
            await CreateRoleAsync(admin, role, "NOSUPERUSER BYPASSRLS");

        var status = await PostgresConnection.GetAppRoleStatusAsync(
            WithPassword(WithUsername(_postgres.ConnectionString, role), ProbePassword));

        Assert.True(status.Verified);
        Assert.Equal(role, status.RoleName);
        Assert.False(status.Superuser);
        Assert.True(status.BypassRls);
        Assert.True(PostgresConnection.IsUnsafe(status));

        await DropRoleAsync(role);
    }

    [Fact]
    public async Task Guard_SameLoginAsMigrationSuperuser_IsReportedUnsafe()
    {
        // The audit scenario: a legacy self-host configuration where the application
        // connection points at the migration superuser login.
        _postgres.RequirePostgres();

        var status = await PostgresConnection.GetAppRoleStatusAsync(_postgres.ConnectionString);

        Assert.True(status.Verified);
        Assert.Equal(PostgresConnection.SuperuserName, status.RoleName);
        Assert.True(status.Superuser);
        Assert.True(PostgresConnection.IsUnsafe(status));
    }

    [Fact]
    public async Task Guard_UnreachableServer_FailsClosed()
    {
        _postgres.RequirePostgres();

        var status = await PostgresConnection.GetAppRoleStatusAsync(
            $"Host=localhost;Port=1;Database=beacon;Username=probe;Password={LeakMarker}");

        Assert.False(status.Verified);
        Assert.True(PostgresConnection.IsUnsafe(status));
        AssertDiagnosticLeaksNothing(status);
    }

    [Fact]
    public async Task Guard_SuperuserDiagnostic_ContainsRoleNameNotConnectionDetails()
    {
        _postgres.RequirePostgres();

        var role = $"guard_diag_{Guid.NewGuid():N}";
        await using (var admin = await OpenAdminAsync())
            await CreateRoleAsync(admin, role, "SUPERUSER", password: LeakMarker);

        var probe = WithPassword(WithUsername(_postgres.ConnectionString, role), LeakMarker);
        var status = await PostgresConnection.GetAppRoleStatusAsync(probe);

        Assert.True(status.Verified);
        Assert.True(PostgresConnection.IsUnsafe(status));
        var diagnostic = PostgresConnection.DescribeAppRoleStatus(status);
        Assert.Contains(role, diagnostic);
        Assert.DoesNotContain(LeakMarker, diagnostic);
        Assert.DoesNotContain("Host=", diagnostic);

        await DropRoleAsync(role);
    }

    private void AssertDiagnosticLeaksNothing(AppRoleStatus status)
    {
        Assert.DoesNotContain(LeakMarker, status.FailureReason);
        Assert.DoesNotContain("Host=", status.FailureReason);
        Assert.DoesNotContain(LeakMarker, PostgresConnection.DescribeAppRoleStatus(status));
    }

    private async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task DropRoleAsync(string role)
    {
        // Best-effort cleanup: the fixture cluster is disposable; a leftover probe role must never fail a test.
        try
        {
            await using var admin = new NpgsqlConnection(_postgres.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
        catch
        {
            // intentionally swallowed: cleanup is best-effort
        }
    }

    private static async Task CreateRoleAsync(NpgsqlConnection admin, string role, string privilegeClause, string password = ProbePassword)
    {
        await using var create = new NpgsqlCommand(
            $"CREATE ROLE \"{role}\" LOGIN {privilegeClause} NOCREATEDB NOCREATEROLE PASSWORD '{password}'", admin);
        await create.ExecuteNonQueryAsync();
        await using var grant = new NpgsqlCommand($"GRANT CONNECT ON DATABASE {DatabaseName(admin)} TO \"{role}\"", admin);
        await grant.ExecuteNonQueryAsync();
    }

    private static string WithUsername(string connectionString, string username)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var replaced = false;
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.StartsWith("Username=", StringComparison.OrdinalIgnoreCase))
            {
                result.Add($"Username={username}");
                replaced = true;
            }
            else
            {
                result.Add(part);
            }
        }
        if (!replaced)
            result.Add($"Username={username}");
        return string.Join(';', result);
    }

    private static string WithPassword(string connectionString, string password)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>(parts.Length);
        var replaced = false;
        foreach (var part in parts)
        {
            if (part.StartsWith("Password=", StringComparison.OrdinalIgnoreCase))
            {
                result.Add($"Password={password}");
                replaced = true;
            }
            else
            {
                result.Add(part);
            }
        }
        if (!replaced)
            result.Add($"Password={password}");
        return string.Join(';', result);
    }

    private static string DatabaseName(NpgsqlConnection connection)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        return builder.Database;
    }
}
