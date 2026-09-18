using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using ProjectBeacon.API.Endpoints;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Identity;
using ProjectBeacon.Infrastructure.Data;

namespace ProjectBeacon.Web.Extensions;

public static class EndpointExtensions
{
    public static WebApplication MapEndpoints(this WebApplication app)
    {
        app.MapGet("/health", async (BeaconDbContext db) =>
        {
            var ok = await db.Database.CanConnectAsync();
            return ok
                ? Results.Ok(new { status = "healthy" })
                : Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();
        app.MapGet("/culture", SetCulture).AllowAnonymous();
        app.MapBeaconApi();

        app.MapPost("/v1/auth/cookie-login", async (HttpContext ctx, LoginHandler handler, BeaconDbContext db) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var login = form["login"].ToString();
            var password = form["password"].ToString();
            var result = await handler.HandleAsync(new LoginCommand(new LoginRequest(
                login, password, ctx.Connection.RemoteIpAddress?.ToString())));
            if (!result.Success)
                return Results.Redirect("/login?error=1");

            var returnUrl = form["returnUrl"].ToString();
            if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//") || returnUrl.Contains('\\'))
                returnUrl = "/dashboard";

            var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(
                db, result.Value.UserId, result.Value.IsAdmin);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, result.Value.UserId.ToString()),
                new(ClaimTypes.Name, result.Value.Login),
                new(ClaimTypes.Email, result.Value.Email),
                new("isAdmin", result.Value.IsAdmin.ToString())
            };
            if (projectId is { } pid)
                claims.Add(new Claim("project_id", pid.ToString()));
            if (orgId is { } oid)
                claims.Add(new Claim("org_id", oid.ToString()));

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Redirect(returnUrl);
        }).AllowAnonymous().RequireRateLimiting("auth").DisableAntiforgery();

        app.MapGet("/project/switch", async (HttpContext ctx, BeaconDbContext db) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true
                || !Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)
                || !Guid.TryParse(ctx.Request.Query["projectId"].ToString(), out var target))
                return Results.Redirect("/dashboard");

            var isAdmin = bool.TryParse(ctx.User.FindFirstValue("isAdmin"), out var adminFlag) && adminFlag;
            var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(db, uid, isAdmin, target);
            if (projectId != target)
                return Results.Redirect("/dashboard");

            var identity = new ClaimsIdentity(
                ctx.User.Claims.Where(c => c.Type != "project_id" && c.Type != "org_id"),
                CookieAuthenticationDefaults.AuthenticationScheme);
            identity.AddClaim(new Claim("project_id", target.ToString()));
            if (orgId is { } oid)
                identity.AddClaim(new Claim("org_id", oid.ToString()));

            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });

            var returnUrl = ctx.Request.Query["returnUrl"].ToString();
            if (string.IsNullOrWhiteSpace(returnUrl)
                || !returnUrl.StartsWith("/", StringComparison.Ordinal)
                || returnUrl.StartsWith("//", StringComparison.Ordinal))
                return Results.Redirect("/dashboard");
            return Results.Redirect(returnUrl);
        }).RequireAuthorization();

        return app;
    }

    private static IResult SetCulture(HttpContext ctx, string culture, string? returnUrl)
    {
        var allowed = ServiceCollectionExtensions.GetSupportedCultures();
        if (!allowed.Contains(culture, StringComparer.OrdinalIgnoreCase))
            culture = "en";

        var value = CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture));
        ctx.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            value,
            new CookieOptions
            {
                Path = "/",
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromDays(365)
            });

        var dest = string.IsNullOrWhiteSpace(returnUrl) ? "/dashboard" : returnUrl;
        if (!dest.StartsWith('/') || dest.StartsWith("//", StringComparison.Ordinal))
            dest = "/dashboard";
        return Results.Redirect(dest);
    }
}
