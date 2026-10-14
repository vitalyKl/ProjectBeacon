namespace ProjectBeacon.API.Tests;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

public sealed class PostgresFixture : IAsyncLifetime
{
    public const string SkipEnvVar = "BEACON_TEST_SKIP_POSTGRES";
    public const string SkipReasonText =
        "PostgreSQL integration tests explicitly skipped: BEACON_TEST_SKIP_POSTGRES is set. " +
        "Unset it to run them against Testcontainers or a BEACON_TEST_DATABASE_URL instance.";

    private const string UrlEnvVar = "BEACON_TEST_DATABASE_URL";
    private const string TestcontainersImage = "postgres:16-alpine";

    private PostgreSqlContainer? _container;
    private Exception? _startupFailure;

    public bool Available { get; private set; }

    /// <summary>
    /// Superuser (migration role) connection string. Bypasses RLS.
    /// </summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Runtime role (beacon_app) connection string. Non-superuser, RLS-bound.
    /// </summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public static bool OptedOut
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(SkipEnvVar);
            return value is "1" or "true" or "True";
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (OptedOut)
            return;

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
    /// Call at the top of every PostgreSQL-dependent test. When the operator opted out
    /// via BEACON_TEST_SKIP_POSTGRES, reports a genuine "Skipped" status with a reason.
    /// When the required infrastructure is unavailable, fails with an actionable
    /// message instead of passing vacuously.
    /// </summary>
    public void RequirePostgres()
    {
        if (Available)
            return;

        if (OptedOut)
        {
            Assert.Skip(SkipReasonText);
            return;
        }

        var url = Environment.GetEnvironmentVariable(UrlEnvVar);
        var mechanism = string.IsNullOrEmpty(url) ? $"Testcontainers ({TestcontainersImage})" : UrlEnvVar;
        throw new InvalidOperationException(
            $"PostgreSQL test infrastructure is unavailable. Attempted mechanism: {mechanism}" +
            (_startupFailure is null ? "." : $". Last error: {_startupFailure.Message}") +
            $" Corrective action: start the Docker daemon, or set {UrlEnvVar} to a disposable PostgreSQL instance" +
            $" (never the shared development database), or set {SkipEnvVar}=1 for an explicit, reported skip.");
    }

    public async ValueTask DisposeAsync()
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
