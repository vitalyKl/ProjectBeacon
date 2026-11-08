namespace ProjectBeacon.Infrastructure.Security;

using System.Globalization;
using System.Security.Claims;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Application.Security;

/// <summary>
/// Validates cookie/JWT account claims against current persisted account state, with a
/// short per-process cache. Password changes, disable/enable and admin changes bump
/// SessionVersion; stale credentials stop working after the cache window.
/// </summary>
public static class UserSessionVersionValidator
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public static async Task<bool> ValidateAsync(
        IServiceProvider services,
        ClaimsPrincipal? principal,
        CancellationToken ct = default)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return false;

        var userIdValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var userId) || userId == Guid.Empty)
            return false;

        var versionValue = principal.FindFirstValue("sv");
        var claimVersion = string.IsNullOrEmpty(versionValue)
            ? 0
            : int.TryParse(versionValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : -1;
        if (claimVersion < 0)
            return false;

        var adminValue = principal.FindFirstValue("isAdmin");
        var claimIsAdmin = bool.TryParse(adminValue, out var parsedAdmin) && parsedAdmin;

        var cache = services.GetRequiredService<IUserSessionVersionCache>();
        if (!cache.TryGet(userId, out var state)
            || state is null
            || state.SessionVersion != claimVersion
            || state.IsAdmin != claimIsAdmin)
        {
            var dbFactory = services.GetRequiredService<IBeaconDbFactory>();
            await using var db = dbFactory.CreateDbContext();
            var persisted = await db.Users
                .Where(user => user.Id == userId)
                .Select(user => new UserSessionVersionSnapshot(
                    user.SessionVersion,
                    user.DisabledAt != null,
                    user.IsAdmin))
                .FirstOrDefaultAsync(ct);
            state = persisted;

            if (state is null)
            {
                cache.Invalidate(userId);
                return false;
            }

            cache.Set(userId, state);
        }

        return !state.IsDisabled
            && state.SessionVersion == claimVersion
            && state.IsAdmin == claimIsAdmin;
    }
}
