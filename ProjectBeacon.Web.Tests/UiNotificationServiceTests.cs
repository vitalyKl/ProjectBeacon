namespace ProjectBeacon.Web.Tests;

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Web.Shared;

public sealed class UiNotificationServiceTests
{
    [Fact]
    public void FormatFailure_IncludesOperationContextAndReturnedDetail()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using var provider = CreateProvider();
            var notifications = provider.GetRequiredService<IUiNotificationService>();

            var message = notifications.FormatFailure("Проект недоступен.", "настройки проекта");

            Assert.Contains("настройки проекта", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Проект недоступен.", message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public void FormatFailure_InternalError_DoesNotExposeTechnicalDetails()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using var provider = CreateProvider();
            var notifications = provider.GetRequiredService<IUiNotificationService>();

            var message = notifications.FormatFailure(
                "NpgsqlException: connection refused at db.internal:5432",
                "сохранение настроек",
                ErrorCodes.InternalError);

            Assert.Contains("сохранение настроек", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Диагностический ID:", message, StringComparison.Ordinal);
            Assert.DoesNotContain("NpgsqlException", message, StringComparison.Ordinal);
            Assert.DoesNotContain("db.internal", message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();
        services.AddUiNotifications();
        return services.BuildServiceProvider();
    }
}
