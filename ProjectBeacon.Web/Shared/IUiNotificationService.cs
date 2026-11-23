namespace ProjectBeacon.Web.Shared;

using MudBlazor;

/// <summary>Single UI entry point for user notifications and operation failures.</summary>
public interface IUiNotificationService
{
    void Add(string message, Severity severity);
    void Success(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);

    /// <summary>Shows a failure with operation context and safe application-level details.</summary>
    string? Failure(string? detail, string operation, string? errorCode = null, object? context = null);
    string FormatFailure(string? detail, string operation, string? errorCode = null, object? context = null);
    string LogException(Exception exception, string operation, object? context = null);
    string FormatDiagnosticMessage(string messageResourceKey, string diagnosticId);

    /// <summary>Logs an unexpected exception and shows a localized message with its diagnostic ID.</summary>
    string ReportException(Exception exception, string messageResourceKey, string operation, object? context = null);
}
