using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using ProjectBeacon.Infrastructure.Http;
using ProjectBeacon.Web.Components;
using ProjectBeacon.Web.Extensions;

namespace ProjectBeacon.Web.Extensions;

public static class MiddlewareExtensions
{
    public static WebApplication ConfigureMiddleware(this WebApplication app)
    {
        app.UseBeaconExceptionHandler("/v1");

        // The configuration binder cannot convert config strings to IPAddress/IPNetwork, so the
        // trusted lists are parsed manually. Bind covers the rest (flags, header names, limit).
        var forwardedHeaders = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardedProtoHeaderName = "X-Forwarded-Proto"
        };
        var forwardedHeadersSection = app.Configuration.GetSection("ForwardedHeaders");
        forwardedHeadersSection.Bind(forwardedHeaders);
        foreach (var value in ForwardedHeaderValues(forwardedHeadersSection, "KnownProxies"))
            forwardedHeaders.KnownProxies.Add(IPAddress.Parse(value));
        foreach (var value in ForwardedHeaderValues(forwardedHeadersSection, "KnownNetworks"))
            forwardedHeaders.KnownNetworks.Add(Microsoft.AspNetCore.HttpOverrides.IPNetwork.Parse(value));
        app.UseForwardedHeaders(forwardedHeaders);

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

    private static IEnumerable<string> ForwardedHeaderValues(IConfiguration section, string key)
    {
        var child = section.GetSection(key);
        if (!string.IsNullOrWhiteSpace(child.Value))
        {
            yield return child.Value;
        }
        foreach (var entry in child.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(entry.Value))
            {
                yield return entry.Value;
            }
        }
    }
}
