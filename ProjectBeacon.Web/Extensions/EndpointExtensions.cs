using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using ProjectBeacon.API.Endpoints;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Identity;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Web.Shared;

namespace ProjectBeacon.Web.Extensions;
/// <summary>
/// Maps /health, the culture cookie, the /v1 API, and cookie login. /health checks that the database accepts a connection.
/// </summary>
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

        app.MapPost("/v1/auth/cookie-login", async (
            HttpContext ctx,
            LoginHandler handler,
            BeaconDbContext db,
            IUiNotificationService diagnostics) =>
        {
            try
            {
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                var login = form["login"].ToString();
                var password = form["password"].ToString();
                var result = await handler.HandleAsync(new LoginCommand(new LoginRequest(
                    login, password, ctx.Connection.RemoteIpAddress?.ToString())), ctx.RequestAborted);
                if (!result.Success)
                {
                    // Keep unknown users, bad passwords, lockouts, and disabled accounts indistinguishable.
                    return Results.Redirect("/login?error=invalid_credentials");
                }

                var returnUrl = form["returnUrl"].ToString();
                if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//") || returnUrl.Contains('\\'))
                    returnUrl = "/dashboard";

                var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(
                    db, result.Value!.UserId, result.Value!.IsAdmin);

                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, result.Value.UserId.ToString()),
                    new(ClaimTypes.Name, result.Value.Login),
                    new(ClaimTypes.Email, result.Value.Email),
                    new("isAdmin", result.Value.IsAdmin.ToString()),
                    new("sv", result.Value.SessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                };
                if (projectId is { } pid)
                    claims.Add(new Claim("project_id", pid.ToString()));
                if (orgId is { } oid)
                    claims.Add(new Claim("org_id", oid.ToString()));

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.Redirect(returnUrl);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var diagnosticId = diagnostics.LogException(
                    exception,
                    "cookie login",
                    new { RemoteIpAddress = ctx.Connection.RemoteIpAddress?.ToString() });
                return Results.Redirect(
                    $"/login?error=unavailable&diagnosticId={Uri.EscapeDataString(diagnosticId)}");
            }
        }).AllowAnonymous().RequireRateLimiting("auth").DisableAntiforgery();

        app.MapGet("/project/switch", async (
            HttpContext ctx,
            BeaconDbContext db,
            IUiNotificationService diagnostics) =>
        {
            try
            {
                var actor = ActorContextFactory.FromPrincipal(ctx.User);
                if (ctx.User.Identity?.IsAuthenticated != true
                    || actor.UserId is not { } uid
                    || !Guid.TryParse(ctx.Request.Query["projectId"].ToString(), out var target))
                    return Results.Redirect("/dashboard?projectSwitchError=failed");

                var isAdmin = actor.IsAdmin;
                var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(db, uid, isAdmin, target);
                // Do not disclose whether a foreign project exists or whether access was denied.
                if (projectId != target)
                    return Results.Redirect("/dashboard?projectSwitchError=failed");

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
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var diagnosticId = diagnostics.LogException(
                    exception,
                    "switch active project",
                    new
                    {
                        UserId = ActorContextFactory.FromPrincipal(ctx.User).UserId,
                        ProjectId = ctx.Request.Query["projectId"].ToString()
                    });
                return Results.Redirect(
                    $"/dashboard?projectSwitchError=unavailable&diagnosticId={Uri.EscapeDataString(diagnosticId)}");
            }
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
