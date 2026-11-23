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

    public string? Failure(string? detail, string operation, string? errorCode = null, object? context = null)
    {
        var diagnosticId = UnexpectedFailureDiagnosticId(detail, operation, errorCode, context);
        Add(FormatFailureMessage(detail, operation, errorCode, diagnosticId), Severity.Error);
        return diagnosticId;
    }

    public string FormatFailure(string? detail, string operation, string? errorCode = null, object? context = null)
    {
        var diagnosticId = UnexpectedFailureDiagnosticId(detail, operation, errorCode, context);
        return FormatFailureMessage(detail, operation, errorCode, diagnosticId);
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

    private string FormatFailureMessage(string? detail, string operation, string? errorCode, string? diagnosticId)
    {
        operation = string.IsNullOrWhiteSpace(operation)
            ? _localizer["UiGenericOperation"].Value
            : operation.Trim();

        if (IsGenericError(detail))
            detail = null;

        switch (errorCode)
        {
            case ErrorCodes.Unauthorized:
            case ErrorCodes.Forbidden:
                return _localizer["UiOperationNotAllowed", operation].Value;
            case ErrorCodes.NotFound:
                return _localizer["UiOperationTargetNotFound", operation].Value;
            case ErrorCodes.Conflict:
                return _localizer["UiOperationConflict", operation].Value;
            case ErrorCodes.Unavailable:
            case ErrorCodes.InternalError:
            case ErrorCodes.Unclassified:
                return _localizer["UiOperationUnexpectedFailed", operation, diagnosticId ?? CreateDiagnosticId()].Value;
        }

        return string.IsNullOrWhiteSpace(detail)
            ? _localizer["UiOperationFailed", operation].Value
            : _localizer["UiOperationFailedWithDetail", operation, detail].Value;
    }

    private string? UnexpectedFailureDiagnosticId(string? detail, string operation, string? errorCode, object? context)
    {
        if (errorCode is not (ErrorCodes.Unavailable or ErrorCodes.InternalError or ErrorCodes.Unclassified))
            return null;

        var diagnosticId = CreateDiagnosticId();
        _logger.LogError(
            "UI operation {Operation} returned an unexpected failure. ErrorCode={ErrorCode}, Detail={Detail}, DiagnosticId={DiagnosticId}, Context={Context}",
            operation, errorCode, detail, diagnosticId, context);
        return diagnosticId;
    }

    private bool IsGenericError(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), _localizer["Error"].Value, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value.Trim(), _localizer["AnErrorOccurred"].Value, StringComparison.OrdinalIgnoreCase);

    private static string CreateDiagnosticId() => Guid.NewGuid().ToString("N")[..8];
}
