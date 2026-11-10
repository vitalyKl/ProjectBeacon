namespace ProjectBeacon.API.Auth;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Separates the operator Control Plane host from the customer application and public API.
/// The operator UI and its audit endpoints are reachable only on the configured control host.
/// </summary>
public sealed class ControlHostMiddleware(RequestDelegate next, IConfiguration configuration, IWebHostEnvironment environment)
{
    private const string DefaultControlHost = "control.projectbeacon.tech";

    public async Task InvokeAsync(HttpContext context)
    {
        // Test hosts exercise the API directly without DNS/ingress host routing.
        if (environment.IsEnvironment("Testing"))
        {
            await next(context);
            return;
        }

        var configuredHost = configuration["Control:Host"]
            ?? Environment.GetEnvironmentVariable("CONTROL_HOST")
            ?? DefaultControlHost;
        var controlHost = configuredHost.Trim().TrimEnd('.');
        var requestHost = context.Request.Host.Host.Trim().TrimEnd('.');
        var isControlHost = string.Equals(requestHost, controlHost, StringComparison.OrdinalIgnoreCase);
        var path = context.Request.Path;

        if (!isControlHost)
        {
            if (IsControlOnlyPath(path))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
            return;
        }

        if (path == "/")
        {
            context.Response.Redirect("/admin/audit");
            return;
        }

        if (!IsAllowedOnControlHost(path))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }

    private static bool IsControlOnlyPath(PathString path) =>
        path.StartsWithSegments("/admin")
        || path.StartsWithSegments("/control")
        || path.StartsWithSegments("/v1/admin")
        || string.Equals(path.Value, "/v1/auth/control-cookie-login", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path.Value, "/v1/auth/bootstrap", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path.Value, "/v1/auth/recover-admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path.Value, "/bootstrap", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path.Value, "/recover", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedOnControlHost(PathString path)
    {
        if (path.StartsWithSegments("/_framework")
            || path.StartsWithSegments("/_content")
            || path.StartsWithSegments("/_blazor"))
            return true;

        var exactAllowedRoute =
            string.Equals(path.Value, "/health", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/culture", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/control/login", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/control/bootstrap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/control/recover", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/control/logout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/control/security", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/control/elevate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/admin/audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/admin/operations", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/admin/email", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/admin/organizations")
            || string.Equals(path.Value, "/admin/operators", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/auth/control-cookie-login", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/auth/bootstrap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/auth/recover-admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/admin/audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/admin/audit/export.csv", StringComparison.OrdinalIgnoreCase);
        if (exactAllowedRoute)
            return true;

        // Do not turn the host allowlist into a wildcard for future API/admin routes merely
        // because a route happens to have a file extension such as .csv.
        if (path.StartsWithSegments("/v1")
            || path.StartsWithSegments("/admin")
            || path.StartsWithSegments("/control"))
            return false;

        // Only the shared shell assets are available on this host. Do not allow every
        // extension-bearing path: a future customer page or API route may also end in ".csv".
        return path.StartsWithSegments("/brand")
            || string.Equals(path.Value, "/app.css", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/app-base.css", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/app-controls.css", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/app-workspace.css", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/app-content.css", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/app-shell.css", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/app-chat.css", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/app-chat-responsive.css", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/shell.js", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/timezone.js", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/chat.js", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/favicon.ico", StringComparison.OrdinalIgnoreCase);
    }
}
