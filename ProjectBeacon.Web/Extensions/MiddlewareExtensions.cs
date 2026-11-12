using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using ProjectBeacon.Infrastructure.Http;
using ProjectBeacon.Web.Components;

namespace ProjectBeacon.Web.Extensions;

/// <summary>
/// Orders forwarded headers, the culture cookie, authentication, and the tenant middleware. Known proxies and networks come from ForwardedHeaders configuration.
/// </summary>
public static class MiddlewareExtensions
{
    public static WebApplication ConfigureMiddleware(this WebApplication app)
    {
        app.UseBeaconExceptionHandler("/v1");

        // Chrome blocks unload unless the document opts in. Blazor registers that listener when the circuit starts.
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("Permissions-Policy", "unload=(self)");
            await next();
        });

        app.UseForwardedHeaders(ForwardedHeadersOptionsFactory.Create(app.Configuration));

        var supportedCultures = ServiceCollectionExtensions.GetSupportedCultures();

        var localizationOptions = new RequestLocalizationOptions()
            .SetDefaultCulture("en")
            .AddSupportedCultures(supportedCultures)
            .AddSupportedUICultures(supportedCultures);

        localizationOptions.RequestCultureProviders.Clear();
        localizationOptions.RequestCultureProviders.Add(new QueryStringRequestCultureProvider());
        localizationOptions.RequestCultureProviders.Add(new CookieRequestCultureProvider());

        app.UseRequestLocalization(localizationOptions);

        app.UseHttpsRedirection();
        // Run before static-file handling as well as endpoint routing so the Control Plane
        // host allowlist also constrains existing files, not just application endpoints.
        app.UseMiddleware<ProjectBeacon.API.Auth.ControlHostMiddleware>();
        app.UseStaticFiles();
        app.MapStaticAssets();
        app.UseRouting();
        // Authenticate before rate limiting so trusted user IDs can receive independent
        // buckets in addition to the forwarded client-IP buckets.
        app.UseAuthentication();
        app.UseRateLimiter();
        // Wrap token/device boundaries, tenant isolation and endpoint authorization so every
        // explicit HTTP 403 on a versioned API route is observed.
        app.UseMiddleware<ProjectBeacon.API.Auth.AuditDeniedRequestMiddleware>();
        app.UseMiddleware<ProjectBeacon.API.Auth.ApiTokenAuthMiddleware>();
        app.UseMiddleware<ProjectBeacon.Infrastructure.Http.ApiTokenIdentityRateLimitMiddleware>();
        app.UseMiddleware<ProjectBeacon.API.Auth.DeviceActorBoundaryMiddleware>();
        app.UseMiddleware<ProjectBeacon.Infrastructure.Http.TenantIsolationMiddleware>();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapEndpoints();

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.MapGet("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            ctx.Response.Redirect("/login");
        });

        return app;
    }
}
