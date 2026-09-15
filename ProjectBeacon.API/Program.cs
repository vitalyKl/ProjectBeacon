using ProjectBeacon.Application;
using ProjectBeacon.Infrastructure;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Http;
using ProjectBeacon.API.Auth;
using ProjectBeacon.API.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json.Serialization;

EnvFile.Load();

var builder = WebApplication.CreateBuilder(args);

var connectionString = PostgresConnection.Resolve(builder.Configuration);

var jwtSecret = builder.Configuration["JWT:Secret"]
    ?? throw new InvalidOperationException("JWT secret key not configured.");

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
});

builder.Services.AddAuthorization();
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

builder.Services.AddRateLimiter(options =>
    AuthRateLimiter.Configure(options, rateLimitPerWindow, rateLimitWindowSeconds));

var app = builder.Build();

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ApiTokenAuthMiddleware>();
app.UseMiddleware<TenantIsolationMiddleware>();
app.UseAuthorization();

app.MapBeaconApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program { }
