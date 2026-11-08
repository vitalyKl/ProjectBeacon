namespace ProjectBeacon.Infrastructure.Security;

using Application.Security;
using Microsoft.Extensions.Caching.Memory;

public sealed class UserSessionVersionCache(IMemoryCache cache) : IUserSessionVersionCache
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private static string Key(Guid userId) => $"ProjectBeacon.UserSessionVersion:{userId:D}";

    public bool TryGet(Guid userId, out UserSessionVersionSnapshot? snapshot) =>
        cache.TryGetValue(Key(userId), out snapshot);

    public void Set(Guid userId, UserSessionVersionSnapshot snapshot) =>
        cache.Set(Key(userId), snapshot, CacheDuration);

    public void Invalidate(Guid userId) => cache.Remove(Key(userId));
}
