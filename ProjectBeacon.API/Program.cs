using ProjectBeacon.Application;
using ProjectBeacon.Infrastructure;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Http;
using ProjectBeacon.Infrastructure.Security;
using ProjectBeacon.API.Auth;
using ProjectBeacon.API.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;

EnvFile.Load();

var builder = WebApplication.CreateBuilder(args);

var connectionString = PostgresConnection.Resolve(builder.Configuration);

var jwtSecret = JwtSecretPolicy.EnsureConfigured(
    builder.Configuration["JWT:Secret"],
    builder.Environment.EnvironmentName);

builder.Services.AddInfrastructure(connectionString);
builder.Services.AddApplicationHandlers();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = "ProjectBeacon",
        ValidAudience = "ProjectBeaconAPI",
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(jwtSecret.PadRight(64).Substring(0, 64)))
    };
    options.Events = new JwtBearerEvents
    {
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            await ProblemJson.WriteAsync(
                ctx.HttpContext,
                StatusCodes.Status401Unauthorized,
                "Unauthorized",
                "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                "Authentication is required.");
        }
    };
});

builder.Services.AddAuthorization();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier;
    };
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var rateLimitPerWindow = int.TryParse(
    Environment.GetEnvironmentVariable("RATE_LIMIT_PER_WINDOW"), out var perWindow)
    ? perWindow : 5;

var rateLimitWindowSeconds = int.TryParse(
    Environment.GetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS"), out var windowSecs)
    ? windowSecs : 60;

var rateLimitReadPerWindow = int.TryParse(
    Environment.GetEnvironmentVariable("RATE_LIMIT_READ_PER_WINDOW"), out var readPerWindow)
    ? readPerWindow : 60;

builder.Services.AddRateLimiter(options =>
    AuthRateLimiter.Configure(options, rateLimitPerWindow, rateLimitWindowSeconds, rateLimitReadPerWindow));

var app = builder.Build();

app.UseBeaconExceptionHandler();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ApiTokenAuthMiddleware>();
app.UseMiddleware<DeviceActorBoundaryMiddleware>();
app.UseMiddleware<TenantIsolationMiddleware>();
app.UseAuthorization();

app.MapBeaconApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

if (app.Environment.IsEnvironment("Testing"))
{
    app.MapGet("/v1/__test/unhandled", _ => throw new InvalidOperationException("test unhandled exception"));
}

app.Run();

/// <summary>
/// API host entry point. Calls <c>MapBeaconApi</c> so every <c>/v1</c> group is mapped, and maps <c>GET /health</c> with no auth filter in this file.
/// In the Testing environment also maps <c>GET /v1/__test/unhandled</c> with no auth filter in this file.
/// </summary>
public partial class Program { }
