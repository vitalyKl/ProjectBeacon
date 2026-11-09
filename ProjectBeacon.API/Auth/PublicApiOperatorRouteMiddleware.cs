namespace ProjectBeacon.API.Auth;

using Microsoft.AspNetCore.Http;

/// <summary>
/// The standalone public API host never serves platform-operator endpoints.
/// The Control Plane UI host serves its own server-side audit API on control.projectbeacon.tech.
/// </summary>
public sealed class PublicApiOperatorRouteMiddleware(RequestDelegate next, IWebHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (environment.IsEnvironment("Testing"))
        {
            await next(context);
            return;
        }

        var path = context.Request.Path;
        var controlOnly =
            path.StartsWithSegments("/v1/admin")
            || string.Equals(path.Value, "/v1/auth/control-cookie-login", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/auth/bootstrap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Value, "/v1/auth/recover-admin", StringComparison.OrdinalIgnoreCase);

        if (controlOnly)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}
