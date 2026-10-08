namespace ProjectBeacon.API.Auth;

using Application.Authorization;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Request access to the caller resolved from the authenticated principal.
/// </summary>
public static class ActorContextHttpExtensions
{
    private const string CacheKey = "ProjectBeacon.ActorContext";

    /// <summary>
    /// Returns the caller for this request, resolved once from <c>ActorContextFactory.FromPrincipal</c> and cached on the context.
    /// Body user ids are not the caller.
    /// </summary>
    public static ActorContext GetActor(this HttpContext context)
    {
        if (context.Items.TryGetValue(CacheKey, out var cached) && cached is ActorContext actor)
            return actor;

        var fresh = ActorContextFactory.FromPrincipal(context.User);
        context.Items[CacheKey] = fresh;
        return fresh;
    }
}
