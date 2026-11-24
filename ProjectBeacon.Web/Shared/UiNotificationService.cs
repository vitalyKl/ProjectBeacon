namespace ProjectBeacon.Web.Shared;

using MudBlazor;
using Microsoft.Extensions.Localization;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Web.Resources;

internal sealed class UiNotificationService : IUiNotificationService
{
    private readonly ISnackbar _snackbar;
    private readonly IStringLocalizer _localizer;
    private readonly ILogger<UiNotificationService> _logger;

    public UiNotificationService(
        ISnackbar snackbar,
        IStringLocalizerFactory localizerFactory,
        ILogger<UiNotificationService> logger)
    {
        _snackbar = snackbar;
        _localizer = localizerFactory.Create(typeof(Web));
        _logger = logger;
    }

    public void Add(string message, Severity severity)
    {
        if (string.IsNullOrWhiteSpace(message))
            message = _localizer["UiEmptyNotification"].Value;
        else if (severity == Severity.Error && IsGenericError(message))
            message = _localizer["UiGenericFailure"].Value;

        _snackbar.Add(message, severity);
    }

    public void Success(string message) => Add(message, Severity.Success);
    public void Info(string message) => Add(message, Severity.Info);
    public void Warning(string message) => Add(message, Severity.Warning);
    public void Error(string message) => Add(message, Severity.Error);

    public string? Failure(Result result, string section, string? operationLabel = null, object? context = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Success
            ? null
            : Failure(result.Error, section, result.ErrorCode ?? ErrorCodes.For(result.Kind), operationLabel, context);
    }

    public string? Failure<T>(Result<T> result, string section, string? operationLabel = null, object? context = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Success
            ? null
            : Failure(result.Error, section, result.ErrorCode ?? ErrorCodes.For(result.Kind), operationLabel, context);
    }

    public string? Failure(string? detail, string section, string? errorCode = null, string? operationLabel = null, object? context = null)
    {
        var diagnosticId = UnexpectedFailureDiagnosticId(detail, section, operationLabel, errorCode, context);
        Add(FormatFailureMessage(detail, section, errorCode, diagnosticId, operationLabel), Severity.Error);
        return diagnosticId;
    }

    public string FormatFailure(Result result, string section, string? operationLabel = null, object? context = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Success
            ? string.Empty
            : FormatFailure(result.Error, section, result.ErrorCode ?? ErrorCodes.For(result.Kind), operationLabel, context);
    }

    public string FormatFailure<T>(Result<T> result, string section, string? operationLabel = null, object? context = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Success
            ? string.Empty
            : FormatFailure(result.Error, section, result.ErrorCode ?? ErrorCodes.For(result.Kind), operationLabel, context);
    }

    public string FormatFailure(string? detail, string section, string? errorCode = null, string? operationLabel = null, object? context = null)
    {
        var diagnosticId = UnexpectedFailureDiagnosticId(detail, section, operationLabel, errorCode, context);
        return FormatFailureMessage(detail, section, errorCode, diagnosticId, operationLabel);
    }

    public string FormatDiagnosticMessage(string messageResourceKey, string diagnosticId) =>
        $"{_localizer[messageResourceKey].Value}{Environment.NewLine}{_localizer["DiagnosticId", diagnosticId].Value}";

    public string LogException(Exception exception, string operation, object? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var diagnosticId = CreateDiagnosticId();
        _logger.LogError(exception,
            "UI operation {Operation} failed. DiagnosticId={DiagnosticId}, Context={Context}",
            operation, diagnosticId, context);
        return diagnosticId;
    }

    public string ReportException(Exception exception, string messageResourceKey, string operation, object? context = null)
    {
        var diagnosticId = LogException(exception, operation, context);
        Add(FormatDiagnosticMessage(messageResourceKey, diagnosticId), Severity.Error);
        return diagnosticId;
    }

    public string FormatUnknownReasonCode(string reasonCode, string messageResourceKey, string operation, object? context = null)
    {
        var diagnosticId = CreateDiagnosticId();
        _logger.LogWarning(
            "UI received an unrecognized operation reason code {ReasonCode}. Operation={Operation}, DiagnosticId={DiagnosticId}, Context={Context}",
            reasonCode, operation, diagnosticId, context);
        return FormatDiagnosticMessage(messageResourceKey, diagnosticId);
    }

    private string FormatFailureMessage(string? detail, string section, string? errorCode, string? diagnosticId, string? operationLabel)
    {
        section = string.IsNullOrWhiteSpace(section) ? _localizer["UiGenericOperation"].Value : section.Trim();
        operationLabel = string.IsNullOrWhiteSpace(operationLabel) ? _localizer["UiGenericOperation"].Value : operationLabel.Trim();

        if (IsGenericError(detail))
            detail = null;

        switch (errorCode)
        {
            case ErrorCodes.Unauthorized:
                return _localizer["UiOperationAuthenticationRequired", section, operationLabel].Value;
            case ErrorCodes.Forbidden:
                return _localizer["UiOperationNotAllowed", section, operationLabel].Value;
            case ErrorCodes.NotFound:
                return _localizer["UiOperationTargetNotFound", section, operationLabel].Value;
            case ErrorCodes.Conflict:
                return _localizer["UiOperationConflict", section, operationLabel].Value;
            case ErrorCodes.InviteEmailMismatch:
                return _localizer["InviteEmailMismatch", section, operationLabel].Value;
            case ErrorCodes.InviteInvalidOrExpired:
                return _localizer["InviteInvalidOrExpired"].Value;
            case ErrorCodes.Unavailable:
                return _localizer["UiOperationUnavailable", section, operationLabel, diagnosticId ?? CreateDiagnosticId()].Value;
            case ErrorCodes.InternalError:
            case ErrorCodes.Unclassified:
                return _localizer["UiOperationUnexpectedFailed", section, operationLabel, diagnosticId ?? CreateDiagnosticId()].Value;
            case null:
            case ErrorCodes.ValidationFailed:
                return string.IsNullOrWhiteSpace(detail)
                    ? _localizer["UiOperationFailed", section, operationLabel].Value
                    : _localizer["UiOperationFailedWithDetail", section, operationLabel, detail].Value;
            default:
                return _localizer["UiOperationUnexpectedFailed", section, operationLabel, diagnosticId ?? CreateDiagnosticId()].Value;
        }
    }

    private string? UnexpectedFailureDiagnosticId(string? detail, string section, string? operationLabel, string? errorCode, object? context)
    {
        if (errorCode is null or ErrorCodes.ValidationFailed
            or ErrorCodes.Unauthorized or ErrorCodes.Forbidden or ErrorCodes.NotFound or ErrorCodes.Conflict
            or ErrorCodes.InviteEmailMismatch or ErrorCodes.InviteInvalidOrExpired)
            return null;

        var diagnosticId = CreateDiagnosticId();
        _logger.LogError(
            "UI operation {Section}/{Operation} returned an unexpected failure. ErrorCode={ErrorCode}, Detail={Detail}, DiagnosticId={DiagnosticId}, Context={Context}",
            section, operationLabel, errorCode, detail, diagnosticId, context);
        return diagnosticId;
    }

    private bool IsGenericError(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), _localizer["Error"].Value, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value.Trim(), _localizer["AnErrorOccurred"].Value, StringComparison.OrdinalIgnoreCase);

    private static string CreateDiagnosticId() => Guid.NewGuid().ToString("N")[..8];
}
