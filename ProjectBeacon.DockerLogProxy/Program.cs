using System.Net.Sockets;
using System.Text.Json.Serialization;
using ProjectBeacon.Application.Operations;
using ProjectBeacon.DockerLogProxy;

var builder = WebApplication.CreateBuilder(args);
var options = DockerLogProxyOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ReplayNonceStore>();
builder.Services.AddSingleton<HttpClient>(sp =>
{
    var proxyOptions = sp.GetRequiredService<DockerLogProxyOptions>();
    var handler = new SocketsHttpHandler
    {
        ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(proxyOptions.DockerSocketPath),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };
    return new HttpClient(handler, disposeHandler: true)
    {
        BaseAddress = new Uri("http://docker"),
        Timeout = TimeSpan.FromSeconds(15)
    };
});
builder.Services.AddSingleton<DockerEngineLogReader>();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.UseMiddleware<DockerLogProxySignatureMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/internal/v1/logs/{service}", async (
    HttpContext context,
    string service,
    DockerEngineLogReader reader,
    DockerLogProxyOptions proxyOptions,
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
    if (tail is < 1 || tail > proxyOptions.MaximumTail)
        return Results.BadRequest(new { error = $"tail must be between 1 and {proxyOptions.MaximumTail}." });

    try
    {
        var snapshot = await reader.ReadAsync(target, tail, ct);
        return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            title: "Docker Engine log retrieval is unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (InvalidDataException)
    {
        return Results.Problem(
            title: "Docker Engine returned an unsupported log stream.",
            statusCode: StatusCodes.Status502BadGateway);
    }
}).WithName("GetAllowlistedContainerLogs");

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
