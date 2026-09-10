namespace ProjectBeacon.API.Auth;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed class ApiTokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiTokenAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, BeaconDbContext db)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer bcn_", StringComparison.OrdinalIgnoreCase))
        {
            var raw = header["Bearer ".Length..].Trim();
            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            var token = await db.ApiTokens.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.TokenHash == hash);

            if (token is not null && !token.IsExpired)
            {
                token.RecordUsage();
                await db.SaveChangesAsync();
                var identity = new ClaimsIdentity("ApiToken");
                identity.AddClaim(new Claim("token_id", token.Id.ToString()));
                identity.AddClaim(new Claim("project_id", token.ProjectId.ToString()));
                identity.AddClaim(new Claim("capabilities", ((long)token.Capabilities).ToString()));
                ctx.User = new ClaimsPrincipal(identity);
            }
        }

        await _next(ctx);
    }
}

public static class CapabilityExtensions
{
    public static bool HasApiCapability(this ClaimsPrincipal user, ApiTokenCapability required)
    {
        if (user.Identity?.AuthenticationType != "ApiToken")
            return true;

        var raw = user.FindFirst("capabilities")?.Value;
        if (!long.TryParse(raw, out var bits))
            return false;

        var caps = (ApiTokenCapability)bits;
        return caps.HasFlag(ApiTokenCapability.Admin) || caps.HasFlag(required);
    }

    public static RouteHandlerBuilder RequireCapability(this RouteHandlerBuilder builder, ApiTokenCapability capability)
    {
        return builder.AddEndpointFilter(async (ctx, next) =>
        {
            if (ctx.HttpContext.User.HasApiCapability(capability))
                return await next(ctx);

            return Results.Json(new { error = "Missing capability." }, statusCode: StatusCodes.Status403Forbidden);
        });
    }
}
