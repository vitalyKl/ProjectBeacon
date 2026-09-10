using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MudBlazor.Services;
using ProjectBeacon.Application;
using ProjectBeacon.Infrastructure;
using ProjectBeacon.Web.Startup;

namespace ProjectBeacon.Web.Extensions;

public static class ServiceCollectionExtensions
{
    private static readonly string[] SupportedCultures = ["en", "ru", "de", "ja", "zh"];

    public static IServiceCollection ConfigureServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ProjectBeacon.Infrastructure.Data.PostgresConnection.Resolve(configuration);

        var jwtSecret = configuration["JWT:Secret"]
            ?? throw new InvalidOperationException("JWT secret key not configured.");

        services.Configure<DockerPostgresConfig>(config =>
        {
            config.PostgresPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        });

        services.AddSingleton<DockerPostgresHelper>();

        var rateLimitPerWindow = int.TryParse(
            Environment.GetEnvironmentVariable("RATE_LIMIT_PER_WINDOW"), out var perWindow)
            ? perWindow : 5;

        var rateLimitWindowSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS"), out var windowSecs)
            ? windowSecs : 60;

        services.AddRateLimiter(options =>
            ProjectBeacon.Infrastructure.Http.AuthRateLimiter.Configure(options, rateLimitPerWindow, rateLimitWindowSeconds));

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = "smart";
            options.DefaultAuthenticateScheme = "smart";
            options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddPolicyScheme("smart", "JWT or cookie", options =>
        {
            options.ForwardDefaultSelector = ctx =>
            {
                var header = ctx.Request.Headers.Authorization.ToString();
                if (header.StartsWith("Bearer bcn_", StringComparison.OrdinalIgnoreCase))
                    return CookieAuthenticationDefaults.AuthenticationScheme;
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    return JwtBearerDefaults.AuthenticationScheme;
                return CookieAuthenticationDefaults.AuthenticationScheme;
            };
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(24);
            options.SlidingExpiration = true;
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
            options.Cookie.Name = "BeaconAuth";
        })
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
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

        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        services.AddMudServices();
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddLocalization();
        services.AddInfrastructure(connectionString);
        services.AddApplicationHandlers();
        services.AddHttpContextAccessor();
        services.AddScoped(sp =>
        {
            var nav = sp.GetRequiredService<NavigationManager>();
            return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
        });
        services.AddEndpointsApiExplorer();

        return services;
    }

    public static string[] GetSupportedCultures() => SupportedCultures;
}
