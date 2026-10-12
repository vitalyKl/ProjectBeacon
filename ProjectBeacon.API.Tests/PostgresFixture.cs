namespace ProjectBeacon.API.Tests;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public bool Available { get; private set; }

    /// <summary>
    /// Superuser (migration role) connection string. Bypasses RLS.
    /// </summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Runtime role (beacon_app) connection string. Non-superuser, RLS-bound.
    /// </summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("BEACON_TEST_DATABASE_URL");
        if (!string.IsNullOrEmpty(url))
        {
            ConnectionString = ToNpgsql(url);
            Available = true;
        }
        else if (!IsCi)
        {
            try
            {
                _container = new PostgreSqlBuilder()
                    .WithImage("postgres:16-alpine")
                    .WithDatabase("beacon")
                    .WithUsername("beacon")
                    .WithPassword("beacon")
                    .Build();
                await _container.StartAsync();
                ConnectionString = _container.GetConnectionString();
                Available = true;
            }
            catch
            {
                if (_container is not null)
                    await _container.DisposeAsync();
                return;
            }
        }

        if (!Available)
            return;

        AppConnectionString = PostgresConnection.DeriveAppConnectionString(ConnectionString);

        await using var db = CreateAdminContext();
        await db.Database.EnsureCreatedAsync();

        await PostgresConnection.EnsureAppRoleAsync(AppConnectionString, ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static bool IsCi
    {
        get
        {
            var ci = Environment.GetEnvironmentVariable("CI");
            return string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase);
        }
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
