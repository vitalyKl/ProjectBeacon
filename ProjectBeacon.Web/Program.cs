using ProjectBeacon.Web.Extensions;
using ProjectBeacon.Web.Startup;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureServices(builder.Configuration);

var app = builder.Build();

var dockerConfig = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DockerPostgresConfig>>().Value;
var dockerHelper = app.Services.GetRequiredService<DockerPostgresHelper>();

var dockerReady = dockerHelper.EnsureRunning(dockerConfig);
if (dockerReady)
{
    app.MigrateDatabase();
}

app.ConfigureMiddleware();
app.MapEndpoints();
app.Run();

public partial class Program { }
