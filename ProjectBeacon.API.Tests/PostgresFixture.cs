namespace ProjectBeacon.API.Tests;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

public sealed class PostgresFixture : IAsyncLifetime
{
    private const string UrlEnvVar = "BEACON_TEST_DATABASE_URL";
    private const string TestcontainersImage = "postgres:16-alpine";

    private PostgreSqlContainer? _container;

    public bool Available { get; private set; }

    /// <summary>
    /// Set only when the operator explicitly opted out via BEACON_TEST_SKIP_POSTGRES.
    /// </summary>
    public string? SkipReason { get; private set; }

    /// <summary>
    /// Superuser (migration role) connection string. Bypasses RLS.
    /// </summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Runtime role (beacon_app) connection string. Non-superuser, RLS-bound.
    /// </summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    private Exception? _startupFailure;

    public async Task InitializeAsync()
    {
        if (PostgresTestCase.OptedOut)
        {
            SkipReason = PostgresTestCase.SkipReasonText;
            return;
        }

        try
        {
            var url = Environment.GetEnvironmentVariable(UrlEnvVar);
            if (!string.IsNullOrEmpty(url))
            {
                ConnectionString = ToNpgsql(url);
            }
            else
            {
                _container = new PostgreSqlBuilder()
                    .WithImage(TestcontainersImage)
                    .WithDatabase("beacon")
                    .WithUsername("beacon")
                    .WithPassword("beacon")
                    .Build();
                await _container.StartAsync();
                ConnectionString = _container.GetConnectionString();
            }

            AppConnectionString = PostgresConnection.DeriveAppConnectionString(ConnectionString);

            await using var db = CreateAdminContext();
            await db.Database.EnsureCreatedAsync();

            await PostgresConnection.EnsureAppRoleAsync(AppConnectionString, ConnectionString);

            Available = true;
        }
        catch (Exception ex)
        {
            if (_container is not null)
            {
                try { await _container.DisposeAsync(); }
                catch { /* best-effort cleanup of a half-started container */ }
            }
            _startupFailure = ex;
        }
    }

    /// <summary>
    /// Call at the top of every PostgreSQL-dependent test. Fails explicitly when the
    /// required infrastructure is unavailable. Skips are reported by [PostgresFact]
    /// (see PostgresFactAttribute), so this method never itself reports a skip.
    /// </summary>
    public void RequirePostgres()
    {
        if (Available)
            return;
        if (SkipReason is not null)
            throw new InvalidOperationException(
                $"This test is not marked with [PostgresFact], so it cannot be reported as skipped: {SkipReasonText()}" +
                $" Mark it with [PostgresFact] or unset {PostgresTestCase.SkipEnvVar}.");

        var url = Environment.GetEnvironmentVariable(UrlEnvVar);
        var mechanism = string.IsNullOrEmpty(url) ? $"Testcontainers ({TestcontainersImage})" : UrlEnvVar;
        throw new InvalidOperationException(
            $"PostgreSQL test infrastructure is unavailable. Attempted mechanism: {mechanism}" +
            (_startupFailure is null ? "." : $". Last error: {_startupFailure.Message}") +
            $" Corrective action: start the Docker daemon, or set {UrlEnvVar} to a disposable PostgreSQL instance" +
            $" (never the shared development database), or set {PostgresTestCase.SkipEnvVar}=1 for an explicit, reported skip.");
    }

    private static string SkipReasonText() => PostgresTestCase.SkipReasonText;

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>
    /// Context connected as the runtime role (beacon_app), the same login the application uses.
    /// </summary>
    public BeaconDbContext CreateContext()
    {
        return new BeaconDbContext(CreateOptions(AppConnectionString));
    }

    public BeaconDbFactory CreateFactory()
    {
        return new BeaconDbFactory(CreateOptions(AppConnectionString), null);
    }

    /// <summary>
    /// Context connected as the superuser migration role. Bypasses RLS; use for seeding.
    /// </summary>
    public BeaconDbContext CreateAdminContext()
    {
        return new BeaconDbContext(CreateOptions(ConnectionString));
    }

    private DbContextOptions<BeaconDbContext> CreateOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    private static string ToNpgsql(string url)
    {
        var uri = new Uri(url.Replace("postgres://", "http://", StringComparison.OrdinalIgnoreCase));
        var userInfo = uri.UserInfo.Split(':', 2);
        return $"Host={uri.Host};Port={(uri.Port > 0 ? uri.Port : 5432)};Database={uri.AbsolutePath.Trim('/')};Username={userInfo[0]};Password={(userInfo.Length > 1 ? userInfo[1] : "")}";
    }
}

[CollectionDefinition("postgres-serial", DisableParallelization = true)]
public sealed class PostgresSerialCollection
{
}
