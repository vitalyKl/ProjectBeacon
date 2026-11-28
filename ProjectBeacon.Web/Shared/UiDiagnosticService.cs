namespace ProjectBeacon.Web.Shared;

internal sealed class UiDiagnosticService(ILogger<UiDiagnosticService> logger) : IUiDiagnosticService
{
    public string LogException(Exception exception, string operation, object? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var diagnosticId = Guid.NewGuid().ToString("N")[..8];
        logger.LogError(exception,
            "Operation {Operation} failed. DiagnosticId={DiagnosticId}, Context={Context}",
            operation, diagnosticId, context);
        return diagnosticId;
    }
}
