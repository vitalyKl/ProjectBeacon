using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using ProjectBeacon.API.Endpoints;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.API.Auth;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Identity;
using ProjectBeacon.Application.Security;
using ProjectBeacon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Web.Authentication;

namespace ProjectBeacon.Web.Extensions;
/// <summary>
/// Maps health, culture, the /v1 API, customer cookie login, and the separate operator Control Plane login.
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
        }).AllowAnonymous().RequireRateLimiting("auth").DisableAntiforgery();

        app.MapPost("/v1/auth/control-cookie-login", async (HttpContext ctx, LoginHandler handler) =>
        {
            if (!ControlFormOriginGuard.IsAllowed(ctx))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var form = await ctx.Request.ReadFormAsync();
            var login = form["login"].ToString();
            var password = form["password"].ToString();
            var result = await handler.HandleOperatorAsync(
                new LoginCommand(new LoginRequest(login, password, ctx.Connection.RemoteIpAddress?.ToString())));
            if (!result.Success || result.Value is null || !result.Value.IsAdmin)
                return Results.Redirect("/control/login?error=1");

            var operatorLogin = result.Value;
            // LoginHandler performs the persisted operator check before creating a session.
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, operatorLogin.UserId.ToString()),
                new(ClaimTypes.Name, operatorLogin.Login),
                new(ClaimTypes.Email, operatorLogin.Email),
                new("isAdmin", "true"),
                new("sv", operatorLogin.SessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("control_plane", "true")
            };
            var identity = new ClaimsIdentity(claims, ControlAuthenticationDefaults.Scheme);
            await ctx.SignInAsync(
                ControlAuthenticationDefaults.Scheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
                    AllowRefresh = true
                });

            return Results.Redirect("/admin/audit");
        }).AllowAnonymous().RequireRateLimiting("auth").DisableAntiforgery();

        app.MapPost("/control/elevate", async (
            HttpContext ctx,
            IBeaconDbFactory dbFactory,
            ISecretProtector secrets,
            ITotp totp,
            IAuditWriter auditWriter,
            TimeProvider timeProvider) =>
        {
            if (!ControlFormOriginGuard.IsAllowed(ctx))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var authentication = await ctx.AuthenticateAsync(ControlAuthenticationDefaults.Scheme);
            var principal = authentication.Principal;
            if (!authentication.Succeeded
                || principal?.Identity?.IsAuthenticated != true
                || !string.Equals(principal.FindFirst("control_plane")?.Value, "true", StringComparison.Ordinal))
                return Results.Redirect("/control/login");

            var actor = ActorContextFactory.FromPrincipal(principal);
            if (actor.UserId is not { } userId || !actor.IsHuman)
                return Results.Redirect("/control/login");

            var now = timeProvider.GetUtcNow();
            var tenant = new TenantContext();
            tenant.Assign(null, null, unscoped: true);
            await using var db = dbFactory.CreateDbContext(tenant);
            var user = await db.Users.FirstOrDefaultAsync(item => item.Id == userId, ctx.RequestAborted);
            if (user is null
                || !Enum.TryParse<OperatorRole>(user.OperatorRole, ignoreCase: false, out var operatorRole)
                || !Enum.IsDefined(operatorRole)
                || user.IsDisabled || !user.TotpEnabled
                || string.IsNullOrWhiteSpace(user.TotpSecretCipher))
            {
                auditWriter.Stage(db, AuditEventFactory.ForActor(
                    actor, "platform.control_elevation.denied", "User", userId.ToString(), null, null,
                    outcome: AuditOutcome.Denied,
                    detailsJson: """{"reasonCode":"mfa_not_enabled"}"""));
                await db.SaveChangesAsync(ctx.RequestAborted);
                return Results.Redirect("/control/security?mfa=required");
            }

            if (actor.OperatorElevatedUntilUtc is { } existing && existing > now)
                return Results.Redirect("/admin/organizations");

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var code = form["code"].ToString();
            if (!secrets.TryOpen(user.TotpSecretCipher, out var secret)
                || !totp.Verify(secret, code, now.UtcDateTime))
            {
                auditWriter.Stage(db, AuditEventFactory.ForActor(
                    actor, "platform.control_elevation.denied", "User", userId.ToString(), null, null,
                    outcome: AuditOutcome.Denied,
                    detailsJson: """{"reasonCode":"invalid_authenticator_code"}"""));
                await db.SaveChangesAsync(ctx.RequestAborted);
                return Results.Redirect("/control/security?error=1");
            }

            var elevatedUntil = now.AddMinutes(10);
            auditWriter.Stage(db, AuditEventFactory.ForActor(
                actor, "platform.control_elevation.succeeded", "User", userId.ToString(), null, null,
                outcome: AuditOutcome.Success,
                detailsJson: """{"resultCode":"elevated","durationSeconds":600}"""));
            await db.SaveChangesAsync(ctx.RequestAborted);

            var claims = principal.Claims
                .Where(claim => claim.Type != "control_elevated_until")
                .ToList();
            claims.Add(new Claim("control_elevated_until",
                elevatedUntil.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
            var identity = new ClaimsIdentity(claims, ControlAuthenticationDefaults.Scheme);
            await ctx.SignInAsync(
                ControlAuthenticationDefaults.Scheme,
                new ClaimsPrincipal(identity),
                authentication.Properties ?? new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = now.AddHours(8),
                    AllowRefresh = false
                });

            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Redirect("/admin/organizations");
        }).RequireAuthorization().RequireRateLimiting("auth").DisableAntiforgery();

        app.MapPost("/control/logout", async (
            HttpContext ctx,
            IBeaconDbFactory dbFactory,
            IAuditWriter auditWriter,
            ILoggerFactory loggerFactory) =>
        {
            if (!ControlFormOriginGuard.IsAllowed(ctx))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                var tenant = new TenantContext();
                tenant.Assign(null, null, unscoped: true);
                await using var db = dbFactory.CreateDbContext(tenant);
                auditWriter.Stage(db, AuditEventFactory.ForActor(
                    ctx.GetActor(),
                    "platform.control_logout.succeeded",
                    "User",
                    ctx.GetActor().UserId?.ToString(),
                    null,
                    null,
                    outcome: AuditOutcome.Success,
                    detailsJson: """{"authMethod":"control-cookie"}"""));
                await db.SaveChangesAsync(ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                // A logging outage must not leave a user signed in to the Control Plane.
                loggerFactory.CreateLogger("ProjectBeacon.ControlPlane").LogWarning(ex, "Could not persist the Control Plane logout audit event.");
            }

            await ctx.SignOutAsync(ControlAuthenticationDefaults.Scheme);
            return Results.Redirect("/control/login");
        }).RequireAuthorization().DisableAntiforgery();

        app.MapGet("/project/switch", async (HttpContext ctx, BeaconDbContext db) =>
        {
            var actor = ActorContextFactory.FromPrincipal(ctx.User);
            if (ctx.User.Identity?.IsAuthenticated != true
                || actor.UserId is not { } uid
                || !Guid.TryParse(ctx.Request.Query["projectId"].ToString(), out var target))
                return Results.Redirect("/dashboard");

            var isAdmin = actor.IsAdmin;
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
