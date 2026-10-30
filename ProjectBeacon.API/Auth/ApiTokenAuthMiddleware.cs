namespace ProjectBeacon.API.Auth;

using ProjectBeacon.API;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Application.Authorization;
using Domain.Enums;
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

            var now = DateTime.UtcNow;
            var valid = token is not null && !token.IsExpired && !token.IsRevoked;
            if (valid && token!.ClientKind == ApiTokenClientKind.Harness)
                valid = await IsHarnessTokenActiveAsync(db, token, now, ctx.RequestAborted);

            if (valid && token is not null)
            {
                // Throttle writes: LastUsedAt is approximate and must not turn every API
                // request into a database write for long-running coding-agent traffic.
                if (token.LastUsedAt is null || token.LastUsedAt < now.AddMinutes(-5))
                {
                    token.RecordUsage();
                    await db.SaveChangesAsync();
                }
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

    private static async Task<bool> IsHarnessTokenActiveAsync(
        BeaconDbContext db,
        Domain.Entities.Identity.ApiToken token,
        DateTime utcNow,
        CancellationToken ct)
    {
        var required = ApiTokenCapability.ContextRead | ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite;
        if (token.Capabilities != required
            || token.DeviceId is not { } deviceId
            || token.CommandId is not { } commandId
            || token.ProjectId == Guid.Empty)
            return false;

        var work = await db.WorkstationCommands.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.Id == commandId && c.DeviceId == deviceId)
            .Select(c => new
            {
                c.ProjectId,
                c.Kind,
                c.Status,
                c.StartedAt,
                c.LeaseExpiresAt,
                c.ClaimInstanceId
            })
            .FirstOrDefaultAsync(ct);
        if (work is null
            || work.ProjectId != token.ProjectId
            || work.Kind != WorkstationCommandKind.ChatPrompt
            || work.Status != WorkstationCommandStatus.Running
            || work.StartedAt is null
            || token.CreatedAt < work.StartedAt.Value
            || work.LeaseExpiresAt is not { } leaseExpiresAt
            || leaseExpiresAt <= utcNow)
            return false;

        var device = await db.DaemonDevices.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        return device is not null
            && device.IsOnline(utcNow)
            && string.Equals(work.ClaimInstanceId, device.ActiveInstanceId, StringComparison.Ordinal);
    }
}

