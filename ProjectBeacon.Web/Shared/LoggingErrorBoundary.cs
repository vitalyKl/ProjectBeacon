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

    protected override Task OnErrorAsync(Exception exception)
    {
        Logger.LogError(exception, "Unhandled exception in Blazor route.");
        return Task.CompletedTask;
    }
}
