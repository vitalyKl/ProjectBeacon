namespace ProjectBeacon.Web.Shared;

using Microsoft.Extensions.DependencyInjection;

public static class UiNotificationServiceCollectionExtensions
{
    public static IServiceCollection AddUiNotifications(this IServiceCollection services)
    {
        services.AddScoped<IUiDiagnosticService, UiDiagnosticService>();
        services.AddScoped<IUiNotificationService, UiNotificationService>();
        return services;
    }
}
