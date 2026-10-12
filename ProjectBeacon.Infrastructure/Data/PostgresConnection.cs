namespace ProjectBeacon.Infrastructure.Data;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Npgsql;
/// <summary>
/// Resolves connection strings for the two Postgres roles. The runtime role (beacon_app) is a
/// non-superuser bound by row-level security; the migration role (beacon) is a superuser that
/// applies EF migrations and bootstraps the runtime role.
/// </summary>
public static class PostgresConnection
{
    public const string SuperuserName = "beacon";
    public const string AppRoleName = "beacon_app";

    private static readonly Regex RoleNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static string Resolve(IConfiguration configuration)
    {
        var fromConfig = configuration.GetConnectionString("Default");
        var password = ResolvePassword(configuration);

        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            var withPassword = EnsurePassword(fromConfig, password);
            if (HasPassword(withPassword))
                return withPassword;
        }

        if (!string.IsNullOrWhiteSpace(password))
            return $"Host=localhost;Port=5432;Database=beacon;Username={AppRoleName};Password={password}";

        throw new InvalidOperationException(
            "Set ConnectionStrings:Default (with Password) or POSTGRES_PASSWORD. For local runs, put them in .env at the repo root.");
    }

    public static string ResolveMigration(IConfiguration configuration)
    {
        var fromMigration = configuration.GetConnectionString("Migration");
        var fromDefault = configuration.GetConnectionString("Default");
        var password = ResolvePassword(configuration);

        string baseString;
        if (!string.IsNullOrWhiteSpace(fromMigration))
            baseString = fromMigration;
        else if (!string.IsNullOrWhiteSpace(fromDefault))
            baseString = WithUsername(fromDefault, SuperuserName);
        else if (!string.IsNullOrWhiteSpace(password))
            baseString = $"Host=localhost;Port=5432;Database=beacon;Username={SuperuserName}";
        else
            throw new InvalidOperationException(
                "Set ConnectionStrings:Migration, or ConnectionStrings:Default plus POSTGRES_PASSWORD. For local runs, put them in .env at the repo root.");

        return RequirePassword(EnsurePassword(baseString, password));
    }

    /// <summary>
    /// Resolves the migration connection string from the process environment only.
    /// Used by the design-time factory where no IConfiguration is available.
    /// </summary>
    public static string ResolveMigrationFromEnvironment()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        return ResolveMigration(configuration);
    }

    /// <summary>
    /// Derives a runtime role connection string from a superuser one: same server and database,
    /// runtime role login.
    /// </summary>
    public static string DeriveAppConnectionString(string migrationConnectionString)
        => WithUsername(migrationConnectionString, AppRoleName);

    /// <summary>
    /// Idempotently creates the runtime role and grants it schema, table, and sequence privileges
    /// in the database the migration connection points at. Skipped when both roles are the same login.
    /// </summary>
    public static async Task EnsureAppRoleAsync(IConfiguration configuration)
    {
        await EnsureAppRoleAsync(Resolve(configuration), ResolveMigration(configuration));
    }

    public static async Task EnsureAppRoleAsync(string appConnectionString, string migrationConnectionString)
    {
        var app = new NpgsqlConnectionStringBuilder(appConnectionString);
        var migration = new NpgsqlConnectionStringBuilder(migrationConnectionString);

        if (string.Equals(app.Username, migration.Username, StringComparison.OrdinalIgnoreCase))
            return;

        var role = app.Username ?? AppRoleName;
        var password = app.Password ?? string.Empty;

        await using var connection = new NpgsqlConnection(migrationConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = BuildAppRoleBootstrap(role, password);
        await command.ExecuteNonQueryAsync();
    }

    private static string BuildAppRoleBootstrap(string role, string password)
    {
        var quoted = QuoteIdentifier(role);
        var sqlPassword = password.Replace("'", "''");

        return $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{role}') THEN
                    CREATE ROLE {quoted} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
                END IF;
            END
            $$;
            ALTER ROLE {quoted} WITH LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD '{sqlPassword}';
            GRANT USAGE ON SCHEMA public TO {quoted};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {quoted};
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {quoted};
            ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {quoted};
            ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {quoted};
            """;
    }

    private static string QuoteIdentifier(string name)
    {
        if (!RoleNamePattern.IsMatch(name))
            throw new InvalidOperationException($"Unsafe Postgres role name: {name}");
        return "\"" + name + "\"";
    }

    private static string? ResolvePassword(IConfiguration configuration)
    {
        var password = FirstNonEmpty(
            Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"),
            configuration["POSTGRES_PASSWORD"]);

        if (string.IsNullOrWhiteSpace(password) && IsDevelopment())
            password = "beacon";

        return password;
    }

    private static string EnsurePassword(string connectionString, string? password)
    {
        if (HasPassword(connectionString) || string.IsNullOrWhiteSpace(password))
            return connectionString;
        return connectionString.TrimEnd(';') + ";Password=" + password;
    }

    private static string RequirePassword(string connectionString)
    {
        if (!HasPassword(connectionString))
            throw new InvalidOperationException(
                "The migration connection string has no password and POSTGRES_PASSWORD is not set.");
        return connectionString;
    }

    private static string WithUsername(string connectionString, string username)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var replaced = false;
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.StartsWith("Username=", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith("User ID=", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith("User Id=", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("Username=" + username);
                replaced = true;
            }
            else
            {
                result.Add(part);
            }
        }

        if (!replaced)
            result.Add("Username=" + username);

        return string.Join(';', result);
    }

    private static bool IsDevelopment()
    {
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        return string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool HasPassword(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("Password=", StringComparison.OrdinalIgnoreCase) &&
                part.Length > "Password=".Length)
                return true;
        }

        return false;
    }
}
