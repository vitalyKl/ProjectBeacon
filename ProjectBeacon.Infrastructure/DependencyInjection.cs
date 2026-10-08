namespace ProjectBeacon.Infrastructure;

using Application.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.LlamaSwap;
using ProjectBeacon.Infrastructure.Mail;
using ProjectBeacon.Infrastructure.Security;
/// <summary>
/// Registers the database, mail, password hashing, JWT, TOTP, and secret protection used by the hosts.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddDbContext<BeaconDbContext>(options =>
            options.UseNpgsql(connectionString, o => o.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
                .AddInterceptors(new TenantRlsConnectionInterceptor()));
        services.AddBeaconDbFactory();
        services.AddApplicationPorts();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        return services;
    }

    public static IServiceCollection AddBeaconDbFactory(this IServiceCollection services)
    {
        services.AddScoped<IDbContextFactory<BeaconDbContext>, BeaconDbFactory>();
        services.AddScoped<IBeaconDbFactory>(sp => (IBeaconDbFactory)sp.GetRequiredService<IDbContextFactory<BeaconDbContext>>());
        return services;
    }

    public static IServiceCollection AddApplicationPorts(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddSingleton<ITotp, RfcTotp>();
        if (services.All(d => d.ServiceType != typeof(ILlamaSwapCatalog)))
        {
            var port = int.TryParse(Environment.GetEnvironmentVariable("BEACON_LLAMASWAP_PORT"), out var p) ? p : 8080;
            services.AddSingleton<ILlamaSwapCatalog>(_ => new LlamaSwapCatalog(port));
        }
        return services;
    }
}
