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

        // Public static resources are shared, but application pages and APIs are not.
        if (Path.HasExtension(path.Value))
            return true;

        return string.Equals(path.Value, "/health", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/culture", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/control/login", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/control/bootstrap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/control/recover", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/control/logout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/admin/audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/auth/control-cookie-login", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/auth/bootstrap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/auth/recover-admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/admin/audit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/admin/audit/export.csv", StringComparison.OrdinalIgnoreCase);
    }
}
