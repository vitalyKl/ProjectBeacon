namespace ProjectBeacon.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Mail;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddDbContext<BeaconDbContext>(options =>
            options.UseNpgsql(connectionString, o => o.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));
        services.AddScoped<IDbContextFactory<BeaconDbContext>, BeaconDbFactory>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        return services;
    }
}
