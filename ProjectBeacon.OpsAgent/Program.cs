using System.Text.Json.Serialization;
using ProjectBeacon.OpsAgent;
using ProjectBeacon.Application.Operations;

var builder = WebApplication.CreateBuilder(args);
var options = OpsAgentOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ReplayNonceStore>();
builder.Services.AddHttpClient<DockerLogProxyClient>(client =>
{
    client.BaseAddress = options.DockerLogProxyBaseUrl;
    client.Timeout = TimeSpan.FromSeconds(12);
});
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.UseMiddleware<OpsAgentSignatureMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous();

app.MapGet("/internal/v1/logs/{service}", async (
    HttpContext context,
    string service,
    DockerLogProxyClient logProxy,
    OpsAgentOptions agentOptions,
    CancellationToken ct) =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (!TryParseTarget(service, out var target))
        return Results.BadRequest(new { error = "Unsupported log service." });

    var tailText = context.Request.Query["tail"].ToString();
    var tail = string.IsNullOrWhiteSpace(tailText)
        ? 200
        : int.TryParse(tailText, out var parsed) ? parsed : -1;
    if (tail is < 1 || tail > agentOptions.MaximumTail)
        return Results.BadRequest(new { error = $"tail must be between 1 and {agentOptions.MaximumTail}." });

    try
    {
        var snapshot = await logProxy.GetLogsAsync(target, tail, ct);
        return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            title: "The restricted Docker log proxy is unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (TaskCanceledException)
    {
        return Results.Problem(
            title: "The restricted Docker log proxy timed out.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (InvalidDataException)
    {
        return Results.Problem(
            title: "The restricted Docker log proxy returned an invalid response.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.Problem(
            title: "The restricted Docker log proxy returned an invalid response.",
            statusCode: StatusCodes.Status502BadGateway);
    }
});

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

public partial class Program { }
