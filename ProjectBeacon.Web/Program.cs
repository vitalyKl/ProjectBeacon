using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Web.Extensions;
using ProjectBeacon.Web.Startup;

EnvFile.Load();

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureServices(builder.Configuration, builder.Environment.EnvironmentName);

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
{
    if (app.Environment.IsDevelopment())
    {
        var dockerConfig = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DockerPostgresConfig>>().Value;
        var dockerHelper = app.Services.GetRequiredService<DockerPostgresHelper>();
        if (!dockerHelper.EnsureRunning(dockerConfig))
        {
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("Docker PostgreSQL helper did not start a container. Migrating against ConnectionStrings:Default anyway.");
        }
    }

    if (ShouldMigrateOnStart(app.Environment))
    {
        await app.MigrateDatabase();
        await app.ReEncryptDataKeys();
    }

    // Fail-closed role guard: in Production the host aborts when the application role
    // has SUPERUSER/BYPASSRLS or cannot be verified. Runs for every Postgres-backed serving
    // host, including Production pods with BEACON_MIGRATE_ON_START=false, and the migrate Job.
    // Test hosts run on in-memory SQLite and have no RLS boundary to verify.
    var appConnectionString = PostgresConnection.Resolve(app.Services.GetRequiredService<IConfiguration>());
    if (PostgresConnection.IsPostgresBacked(app.Services))
        await PostgresConnection.EnforceAppRoleAsync(
            appConnectionString,
            app.Environment.IsProduction(),
            app.Services.GetRequiredService<ILogger<Program>>());

    if (IsTruthy(Environment.GetEnvironmentVariable("BEACON_MIGRATE_THEN_EXIT")))
        return;
}

app.ConfigureMiddleware();
app.Run();

static bool ShouldMigrateOnStart(IHostEnvironment env)
{
    var raw = Environment.GetEnvironmentVariable("BEACON_MIGRATE_ON_START");
    if (string.IsNullOrWhiteSpace(raw))
        return !env.IsProduction();
    return IsTruthy(raw);
}

static bool IsTruthy(string? value) =>
    string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
/// <summary>
/// Web host entry. In Development it starts beacon-postgres. It migrates unless the environment is Production or BEACON_MIGRATE_ON_START is false. BEACON_MIGRATE_THEN_EXIT migrates and returns.
/// </summary>
public partial class Program { }
