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

            var failure = Result.Failure(
                "Проект недоступен.",
                ErrorCodes.ValidationFailed,
                ErrorKind.Validation);
            var message = notifications.FormatFailure(
                failure,
                "Настройки проекта",
                "Загрузить");

            Assert.Contains("Настройки проекта", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Загрузить", message, StringComparison.OrdinalIgnoreCase);
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

    [Theory]
    [InlineData(ErrorCodes.Unauthorized, "войдите")]
    [InlineData(ErrorCodes.Forbidden, "Недостаточно прав")]
    [InlineData(ErrorCodes.NotFound, "не найден")]
    [InlineData(ErrorCodes.Conflict, "конфликт")]
    public void FormatFailure_MapsBusinessErrorCodesToSpecificMessages(string errorCode, string expectedText)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using var context = CreateContext();
            var notifications = context.Services.GetRequiredService<IUiNotificationService>();

            var message = notifications.FormatFailure(
                "A backend detail that is not needed for this error category.",
                "сохранение шаблона",
                errorCode,
                "Сохранить");

            Assert.Contains("сохранение шаблона", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Сохранить", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expectedText, message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public void FormatFailure_Unavailable_ExplainsTemporaryOutageAndIncludesDiagnosticId()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using var context = CreateContext();
            var notifications = context.Services.GetRequiredService<IUiNotificationService>();

            var message = notifications.FormatFailure(
                "Gateway timeout at internal-service:8080",
                "загрузка рабочих станций",
                ErrorCodes.Unavailable,
                "Загрузить");

            Assert.Contains("временно недоступен", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Загрузить", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Диагностический ID:", message, StringComparison.Ordinal);
            Assert.DoesNotContain("internal-service", message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public void FormatUnknownReasonCode_UsesLocalizedFallbackAndDoesNotExposeMachineCode()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using var context = CreateContext();
            var notifications = context.Services.GetRequiredService<IUiNotificationService>();
            var message = notifications.FormatUnknownReasonCode(
                "FutureLandingFailureCode", "LandingReasonUnknown", "display Git landing reason");
            Assert.Contains("не распознаёт", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Диагностический ID:", message, StringComparison.Ordinal);
            Assert.DoesNotContain("FutureLandingFailureCode", message, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentUICulture = previousCulture; }
    }


    [Fact]
    public void FormatFailure_ResultOverload_UsesMachineReadableErrorCode()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using var context = CreateContext();
            var notifications = context.Services.GetRequiredService<IUiNotificationService>();
            var result = Result.Failure<string>("internal detail should be ignored", ErrorCodes.Conflict, ErrorKind.Conflict);

            var message = notifications.FormatFailure(result, "настройки проекта", "Сохранить");

            Assert.Contains("конфликт", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("настройки проекта", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Сохранить", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("internal detail", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public void FormatFailure_InviteEmailMismatch_ShowsSafeSpecificExplanation()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using var context = CreateContext();
            var notifications = context.Services.GetRequiredService<IUiNotificationService>();

            var message = notifications.FormatFailure(
                "Email does not match invite.",
                "приглашения",
                ErrorCodes.InviteEmailMismatch,
                "Принять");

            Assert.Contains("другой адрес электронной почты", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Принять", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Email does not match invite.", message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public void FormatFailure_InviteInvalidOrExpired_UsesLocalizedRecoveryInstruction()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using var context = CreateContext();
            var notifications = context.Services.GetRequiredService<IUiNotificationService>();

            var message = notifications.FormatFailure(
                "Invite is no longer valid.",
                "приглашения",
                ErrorCodes.InviteInvalidOrExpired,
                "Принять");

            Assert.Contains("недействительно", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("попросите отправителя прислать новое приглашение", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Invite is no longer valid.", message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public void FormatFailure_UnknownMachineCode_DoesNotExposeBackendDetail()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using var context = CreateContext();
            var notifications = context.Services.GetRequiredService<IUiNotificationService>();

            var message = notifications.FormatFailure(
                "SQLSTATE 08006 at private-db.internal",
                "загрузка приглашения",
                "future_error_code",
                "Загрузить");

            Assert.Contains("Диагностический ID:", message, StringComparison.Ordinal);
            Assert.DoesNotContain("SQLSTATE", message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-db.internal", message, StringComparison.Ordinal);
            Assert.DoesNotContain("future_error_code", message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("")]
    public void FormatFailure_GenericDetailWithoutErrorCode_ProducesDiagnosticId(string? detail)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using var context = CreateContext();
            var notifications = context.Services.GetRequiredService<IUiNotificationService>();

            var message = notifications.FormatFailure(detail, "сохранение шаблона");

            Assert.Contains("сохранение шаблона", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Диагностический ID:", message, StringComparison.Ordinal);
            Assert.DoesNotContain("AnErrorOccurred", message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public void FormatFailure_SuccessfulResult_ReturnsNoErrorMessage()
    {
        using var context = CreateContext();
        var notifications = context.Services.GetRequiredService<IUiNotificationService>();
        var result = Result.Ok<string>("saved");

        Assert.Equal(string.Empty, notifications.FormatFailure(result, "настройки проекта", "Сохранить"));
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
