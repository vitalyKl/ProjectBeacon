using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using ProjectBeacon.Infrastructure;
using ProjectBeacon.Web.Startup;

namespace ProjectBeacon.Web.Extensions;

public static class ServiceCollectionExtensions
{
    private static readonly string[] SupportedCultures =
    {
        "en", "ru", "de", "fr", "es", "it", "pt", "nl", "pl", "cs",
        "hu", "ro", "bg", "hr", "sk", "sl", "lt", "lv", "et", "fi",
        "sv", "no", "da", "el", "tr", "zh", "ja", "ko", "ar", "hi",
        "th", "vi", "id", "ms", "tl", "uk", "be", "mk", "sr", "bs",
        "ca", "gl", "eu", "is", "ga", "cy"
    };

    public static IServiceCollection ConfigureServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' not found.");

        services.Configure<DockerPostgresConfig>(config =>
        {
            config.PostgresPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        });

        services.AddSingleton<DockerPostgresHelper>();

        services.AddRateLimiter(options =>
        {
            options.AddConcurrencyLimiter("auth", configure =>
            {
                configure.PermitLimit = 1;
                configure.QueueLimit = 0;
            });

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        services.AddLocalization(opts => opts.ResourcesPath = "Resources");

        services.AddMudServices();
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddInfrastructure(connectionString);
        services.AddEndpointsApiExplorer();

        return services;
    }

    public static string[] GetSupportedCultures() => SupportedCultures;
}
