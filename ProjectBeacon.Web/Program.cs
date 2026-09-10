using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Web.Extensions;
using ProjectBeacon.Web.Startup;

EnvFile.Load();

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureServices(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
{
    var dockerConfig = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DockerPostgresConfig>>().Value;
    var dockerHelper = app.Services.GetRequiredService<DockerPostgresHelper>();
    if (!dockerHelper.EnsureRunning(dockerConfig))
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogWarning("Docker PostgreSQL helper did not start a container. Migrating against ConnectionStrings:Default anyway.");
    }

    app.MigrateDatabase();
}

app.ConfigureMiddleware();
app.Run();

public partial class Program { }
