using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Web.Extensions;
using ProjectBeacon.Web.Startup;

EnvFile.Load();

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureServices(builder.Configuration);

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
        app.MigrateDatabase();

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

public partial class Program { }
