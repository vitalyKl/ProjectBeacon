using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using ProjectBeacon.Web.Components;
using ProjectBeacon.Web.Extensions;

namespace ProjectBeacon.Web.Extensions;

public static class MiddlewareExtensions
{
    public static WebApplication ConfigureMiddleware(this WebApplication app)
    {
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
        app.UseStaticFiles();
        app.MapStaticAssets();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<ProjectBeacon.API.Auth.ApiTokenAuthMiddleware>();
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
