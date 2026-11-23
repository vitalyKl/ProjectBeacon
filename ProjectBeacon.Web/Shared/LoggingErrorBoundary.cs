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

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    public string? DiagnosticId { get; private set; }

    protected override Task OnErrorAsync(Exception exception)
    {
        DiagnosticId = Guid.NewGuid().ToString("N")[..8];
        exception.Data["ProjectBeaconDiagnosticId"] = DiagnosticId;
        var location = Navigation.ToBaseRelativePath(Navigation.Uri);
        var queryIndex = location.IndexOf('?');
        var fragmentIndex = location.IndexOf('#');
        var delimiterIndex = queryIndex < 0 ? fragmentIndex
            : fragmentIndex < 0 ? queryIndex : Math.Min(queryIndex, fragmentIndex);
        if (delimiterIndex >= 0)
            location = location[..delimiterIndex];
        location = string.IsNullOrWhiteSpace(location) ? "/" : "/" + location.TrimStart('/');
        exception.Data["ProjectBeaconErrorLocation"] = location;
        Logger.LogError(exception,
            "Unhandled exception in Blazor route {Location}. DiagnosticId={DiagnosticId}",
            location, DiagnosticId);
        return Task.CompletedTask;
    }
}
