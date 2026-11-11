namespace ProjectBeacon.Infrastructure;

using ProjectBeacon.Application.Entitlements;
using ProjectBeacon.Application.Organizations;
using ProjectBeacon.Application.Operations;
using ProjectBeacon.Infrastructure.Operations;
using Application.Mail;
using Application.Audit;
using Application.Persistence;
using Application.Runtime;
using Application.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Caching.Memory;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.LlamaSwap;
using ProjectBeacon.Infrastructure.Mail;
using ProjectBeacon.Infrastructure.Security;
using ProjectBeacon.Infrastructure.Attachments;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Entitlements;
using ProjectBeacon.Infrastructure.Organizations;
/// <summary>
/// Registers the database, mail, password hashing, JWT, TOTP, and secret protection used by the hosts.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddMemoryCache();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IUserSessionVersionCache, UserSessionVersionCache>();
        services.AddDbContext<BeaconDbContext>(options =>
            options.UseNpgsql(connectionString, o => o.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
                .AddInterceptors(new TenantRlsConnectionInterceptor()));
        services.AddBeaconDbFactory();
        services.AddApplicationPorts();
        services.AddScoped<IAttachmentService, DatabaseAttachmentService>();
        services.AddSingleton<IAuditWriter, EfAuditWriter>();
        services.AddScoped<IAuditReader, EfAuditReader>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<IEntitlementService, PostgresEntitlementService>();
        services.AddScoped<IEntitlementEvaluator>(sp => sp.GetRequiredService<IEntitlementService>());
        services.AddScoped<IOrganizationAdminService, EfOrganizationAdminService>();
        services.AddScoped<IOrganizationDataExportService, EfOrganizationDataExportService>();
        services.AddScoped<IPlatformOperationsService, PlatformOperationsService>();
        services.AddHttpClient<IPlatformContainerLogClient, HttpPlatformContainerLogClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(12));

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
