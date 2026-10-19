namespace ProjectBeacon.Web.Tests;

using System.Net;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>
/// Cross-process persistence of the Data Protection key ring: with the same
/// DATA_PROTECTION_KEY, an auth cookie must survive a host restart and be accepted
/// by a second host sharing the ring database, while a host pointing at a different
/// database must reject it.
/// </summary>
public sealed class DataProtectionRingPersistenceTests : IClassFixture<PostgresWebFixture>, IAsyncLifetime
{
    private const string PrimaryDbName = "beacon_dp_ring_web";
    private const string SecondDbName = "beacon_dp_ring_web_b";

    private readonly PostgresWebFixture _postgres;
    private string _serverConn = string.Empty;

    public DataProtectionRingPersistenceTests(PostgresWebFixture postgres) => _postgres = postgres;

    public ValueTask InitializeAsync()
    {
        _serverConn = WithDatabase(_postgres.ConnectionString, "postgres");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // (a) The ring database outlives the host: the same auth cookie must still be
    // accepted after the host restarts against the same database.
    [Fact]
    public async Task CookieSurvivesHostRestartOnSameDatabase()
    {
        _postgres.RequirePostgres();

        var (adminA, appA) = await FreshDatabaseAsync(PrimaryDbName);

        await using var host1 = new PostgresWebTestFactory(adminA, appA);
        var client1 = host1.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cookie = await LoginAsync(client1);
        await host1.DisposeAsync();

        await using var host2 = new PostgresWebTestFactory(adminA, appA);
        var client2 = host2.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client2.DefaultRequestHeaders.Add("Cookie", cookie);
        var response = await client2.GetAsync("/dashboard", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // (b) Two live hosts sharing the ring database: the first host's cookie must be
    // accepted by the second.
    [Fact]
    public async Task CookieFromFirstHostAcceptedBySecondHostOnSameDatabase()
    {
        _postgres.RequirePostgres();

        var (adminA, appA) = await FreshDatabaseAsync(PrimaryDbName);

        await using var hostA = new PostgresWebTestFactory(adminA, appA);
        var clientA = hostA.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cookie = await LoginAsync(clientA);

        await using var hostB = new PostgresWebTestFactory(adminA, appA);
        var clientB = hostB.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        clientB.DefaultRequestHeaders.Add("Cookie", cookie);
        var response = await clientB.GetAsync("/dashboard", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // (c) Two hosts on different databases: the first host's cookie must be rejected
    // (the rings are not shared) and the host must redirect to the login page.
    [Fact]
    public async Task CookieFromOtherDatabaseIsRejected()
    {
        _postgres.RequirePostgres();

        var (adminA, appA) = await FreshDatabaseAsync(PrimaryDbName);
        await using var hostA = new PostgresWebTestFactory(adminA, appA);
        var clientA = hostA.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cookie = await LoginAsync(clientA);

        var (adminB, appB) = await FreshDatabaseAsync(SecondDbName);
        await using var hostB = new PostgresWebTestFactory(adminB, appB);
        var clientB = hostB.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        clientB.DefaultRequestHeaders.Add("Cookie", cookie);
        var response = await clientB.GetAsync("/dashboard", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // Cookie authentication challenges to the login page with a ReturnUrl query string.
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
    }

    // Each test gets freshly dropped-and-created databases: the bootstrap admin
    // endpoint refuses to run twice against the same database (409), and the
    // ring tables must start empty.
    private async Task<(string Admin, string App)> FreshDatabaseAsync(string dbName)
    {
        // Previous hosts leave pooled connections to these databases; the forced
        // drop below would kill them mid-pool and the next checkout would fail.
        NpgsqlConnection.ClearAllPools();
        await ExecAdminAsync(_serverConn, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE);");
        await ExecAdminAsync(_serverConn, $"CREATE DATABASE {dbName};");
        var admin = WithDatabase(_postgres.ConnectionString, dbName);
        var app = PostgresConnection.DeriveAppConnectionString(admin);
        return (admin, app);
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var password = await WebTestFactory.GetBootstrapPasswordAsync(client);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });
        var response = await client.PostAsync("/v1/auth/cookie-login", form, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(value => value.StartsWith("BeaconAuth=", StringComparison.Ordinal));
        return setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
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

/// <summary>
/// Web host backed by a real Postgres database (per-database connection strings
/// supplied by the test). Unlike WebTestFactory's in-memory SQLite, the Data
/// Protection ring lives in the database, so a second host against the same
/// database sees the same keys.
/// </summary>
public sealed class PostgresWebTestFactory : WebApplicationFactory<ProjectBeacon.Web.Features.Dashboard.Dashboard>
{
    // One shared sealing key across all ring-persistence hosts: the ring differs
    // only by database, never by key.
    private const string DataKey = "Ring-Test-Data-Protection-Key-min-32-chars";

    private readonly string _adminConn;
    private readonly string _appConn;

    public PostgresWebTestFactory(string adminConn, string appConn)
    {
        Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", "test");
        _adminConn = adminConn;
        _appConn = appConn;

        // The schema and the app role must exist before the host starts; both are
        // idempotent, so a second host against the same database is a no-op.
        var options = new DbContextOptionsBuilder<BeaconDbContext>().UseNpgsql(adminConn).Options;
        using var db = new BeaconDbContext(options);
        db.Database.Migrate();
        PostgresConnection.EnsureAppRoleAsync(appConn, adminConn).GetAwaiter().GetResult();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _appConn);
        builder.UseSetting("JWT:Secret", "ProjectBeacon-JWT-Secret-Key-Must-Be-At-Least-32-Characters-Long");
        builder.UseSetting("DATA_PROTECTION_KEY", DataKey);
        builder.UseSetting("BOOTSTRAP_ADMIN_TOKEN", WebTestFactory.BootstrapToken);
    }
}
