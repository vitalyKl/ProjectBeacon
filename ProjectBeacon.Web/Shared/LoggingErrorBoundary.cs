namespace ProjectBeacon.Web.Shared;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

/// <summary>
/// Logs exceptions caught by the route-level Blazor error boundary.
/// </summary>
public sealed class LoggingErrorBoundary : ErrorBoundary
{
    [Inject]
    private ILogger<LoggingErrorBoundary> Logger { get; set; } = default!;

    public string? DiagnosticId { get; private set; }

    protected override Task OnErrorAsync(Exception exception)
    {
        DiagnosticId = Guid.NewGuid().ToString("N")[..8];
        exception.Data["ProjectBeaconDiagnosticId"] = DiagnosticId;
        Logger.LogError(exception,
            "Unhandled exception in Blazor route. DiagnosticId={DiagnosticId}",
            DiagnosticId);
        return Task.CompletedTask;
    }
}
