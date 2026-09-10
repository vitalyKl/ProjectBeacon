namespace ProjectBeacon.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Infrastructure.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddDbContext<BeaconDbContext>(options =>
            options.UseNpgsql(connectionString, o => o.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));

        return services;
    }
}
