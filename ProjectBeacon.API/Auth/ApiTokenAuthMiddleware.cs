namespace ProjectBeacon.API.Auth;

using ProjectBeacon.API;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Application.Authorization;
using Application.Devices;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Authenticates <c>Bearer bcn_</c> API tokens and <c>Bearer bcd_</c> device tokens by replacing <c>HttpContext.User</c> before later middleware.
/// The principal is the caller; body user ids are not. Other authorization headers are left for JWT bearer authentication.
/// </summary>
public sealed class ApiTokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware with the next request delegate.</summary>
    public ApiTokenAuthMiddleware(RequestDelegate next) => _next = next;

    /// <summary>
    /// When the bearer token is <c>bcn_</c> or <c>bcd_</c> and still valid, sets <c>HttpContext.User</c> from that token. Identity is that principal, not a body user id.
    /// </summary>
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
                var identity = new ClaimsIdentity(ActorContextFactory.ApiTokenAuthType);
                identity.AddClaim(new Claim("token_id", token.Id.ToString()));
                identity.AddClaim(new Claim("project_id", token.ProjectId.ToString()));
                identity.AddClaim(new Claim("capabilities", ((long)token.Capabilities).ToString()));
                if (token.CreatedByUserId.HasValue)
                    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, token.CreatedByUserId.Value.ToString()));
                ctx.User = new ClaimsPrincipal(identity);
            }
        }
        else if (header.StartsWith("Bearer bcd_", StringComparison.OrdinalIgnoreCase))
        {
            var raw = header["Bearer ".Length..].Trim();
            var hash = DeviceToken.Hash(raw);
            var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.TokenHash == hash && d.RevokedAt == null);
            if (device is not null)
            {
                var identity = new ClaimsIdentity(ActorContextFactory.DeviceTokenAuthType, ClaimTypes.Name, ClaimTypes.Role);
                identity.AddClaim(new Claim("device_id", device.Id.ToString()));
                identity.AddClaim(new Claim("device_owner_id", device.UserId.ToString()));
                ctx.User = new ClaimsPrincipal(identity);
            }
        }

        await _next(ctx);
    }
}

