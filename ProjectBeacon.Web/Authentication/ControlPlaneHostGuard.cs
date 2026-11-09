namespace ProjectBeacon.Web.Authentication;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Client-circuit guard for Control Plane pages. HTTP middleware is the primary boundary;
/// this also blocks client-side Blazor navigation that does not create a new HTTP request.
/// </summary>
public static class ControlPlaneHostGuard
{
    private const string DefaultControlHost = "control.projectbeacon.tech";

    public static bool IsCurrentHost(NavigationManager navigation, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(configuration);

        var expectedHost = (configuration["Control:Host"]
            ?? Environment.GetEnvironmentVariable("CONTROL_HOST")
            ?? DefaultControlHost).Trim().TrimEnd('.');
        var currentHost = navigation.ToAbsoluteUri(navigation.Uri).Host.Trim().TrimEnd('.');

        return string.Equals(currentHost, expectedHost, StringComparison.OrdinalIgnoreCase);
    }
}
