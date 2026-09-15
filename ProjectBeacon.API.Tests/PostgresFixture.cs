namespace ProjectBeacon.API.Tests;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("BEACON_TEST_DATABASE_URL");
        if (!string.IsNullOrEmpty(url))
        {
            ConnectionString = ToNpgsql(url);
        }
        else
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("beacon")
                .WithUsername("beacon")
                .WithPassword("beacon")
                .Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }

        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    public BeaconDbContext CreateContext()
    {
        return new BeaconDbContext(CreateOptions());
    }

    public IDbContextFactory<BeaconDbContext> CreateFactory()
    {
        return new BeaconDbFactory(CreateOptions(), null);
    }

    private DbContextOptions<BeaconDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(ConnectionString)
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