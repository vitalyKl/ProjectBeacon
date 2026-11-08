namespace ProjectBeacon.Infrastructure.Security;

using System.Globalization;
using System.Security.Claims;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Current security state for a human account.</summary>
public sealed record UserSessionVersionState(int SessionVersion, bool IsDisabled, bool IsAdmin);

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

        var cache = services.GetRequiredService<IMemoryCache>();
        var cacheKey = $"ProjectBeacon.UserSessionVersion:{userId:D}";
        if (!cache.TryGetValue(cacheKey, out UserSessionVersionState? state)
            || state is null
            || state.SessionVersion != claimVersion
            || state.IsAdmin != claimIsAdmin)
        {
            var dbFactory = services.GetRequiredService<IBeaconDbFactory>();
            await using var db = dbFactory.CreateDbContext();
            state = await db.Users
                .Where(user => user.Id == userId)
                .Select(user => new UserSessionVersionState(
                    user.SessionVersion,
                    user.DisabledAt != null,
                    user.IsAdmin))
                .FirstOrDefaultAsync(ct);

            if (state is null)
            {
                cache.Remove(cacheKey);
                return false;
            }

            cache.Set(cacheKey, state, CacheDuration);
        }

        return !state.IsDisabled
            && state.SessionVersion == claimVersion
            && state.IsAdmin == claimIsAdmin;
    }
}
