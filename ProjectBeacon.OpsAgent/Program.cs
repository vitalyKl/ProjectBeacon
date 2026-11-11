using System.Text.Json.Serialization;
using ProjectBeacon.OpsAgent;

var builder = WebApplication.CreateBuilder(args);
var options = OpsAgentOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ReplayNonceStore>();
builder.Services.AddSingleton<DockerJsonLogReader>();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.UseMiddleware<OpsAgentSignatureMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous();

app.MapGet("/internal/v1/logs/{service}", async (
    HttpContext context,
    string service,
    DockerJsonLogReader reader,
    OpsAgentOptions agentOptions,
    CancellationToken ct) =>
{
    if (!TryParseTarget(service, out var target))
        return Results.BadRequest(new { error = "Unsupported log service." });

    var tailText = context.Request.Query["tail"].ToString();
    var tail = string.IsNullOrWhiteSpace(tailText)
        ? 200
        : int.TryParse(tailText, out var parsed) ? parsed : -1;
    if (tail is < 1 || tail > agentOptions.MaximumTail)
        return Results.BadRequest(new { error = $"tail must be between 1 and {agentOptions.MaximumTail}." });

    var snapshot = await reader.ReadAsync(target, tail, ct);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
}).RequireHttpMethod("GET");

app.Run();

static bool TryParseTarget(string service, out PlatformLogTarget target)
{
    target = service switch
    {
        "web" => PlatformLogTarget.Web,
        "worker" => PlatformLogTarget.Worker,
        "migrate" => PlatformLogTarget.Migrate,
        _ => (PlatformLogTarget)(-1)
    };
    return Enum.IsDefined(target);
}

public partial class Program;
