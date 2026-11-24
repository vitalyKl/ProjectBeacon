namespace ProjectBeacon.Web.Shared;

using System.Net;
using MudBlazor;
using ProjectBeacon.Application.Common;

/// <summary>Single UI entry point for user notifications and operation failures.</summary>
public interface IUiNotificationService
{
    void Add(string message, Severity severity);
    void Success(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);

    /// <summary>Shows a failure with operation context and safe application-level details.</summary>
    string? Failure(string? detail, string section, string? errorCode = null, string? operationLabel = null, object? context = null);
    string? Failure(Result result, string section, string? operationLabel = null, object? context = null);
    string? Failure<T>(Result<T> result, string section, string? operationLabel = null, object? context = null);
    string FormatFailure(string? detail, string section, string? errorCode = null, string? operationLabel = null, object? context = null);
    string FormatFailure(Result result, string section, string? operationLabel = null, object? context = null);
    string FormatFailure<T>(Result<T> result, string section, string? operationLabel = null, object? context = null);
    string FormatHttpFailure(HttpStatusCode statusCode, string? detail, string section, string? operationLabel = null, object? context = null);
    string LogException(Exception exception, string operation, object? context = null);
    string FormatDiagnosticMessage(string messageResourceKey, string diagnosticId);
    string FormatUnknownReasonCode(string reasonCode, string messageResourceKey, string operation, object? context = null);

    /// <summary>Logs an unexpected exception and shows a localized message with its diagnostic ID.</summary>
    string ReportException(Exception exception, string messageResourceKey, string operation, object? context = null);
}
