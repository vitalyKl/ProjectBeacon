namespace ProjectBeacon.Web.Tests;

using System.Globalization;
using TestContext = global::Bunit.TestContext;
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
            using var context = CreateContext();
            var notifications = context.Services.GetRequiredService<IUiNotificationService>();

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
            using var context = CreateContext();
            var notifications = context.Services.GetRequiredService<IUiNotificationService>();

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

    private static TestContext CreateContext()
    {
        var context = new TestContext();
        context.Services.AddLogging();
        context.Services.AddLocalization();
        context.Services.AddMudServices();
        context.Services.AddUiNotifications();
        return context;
    }
}
