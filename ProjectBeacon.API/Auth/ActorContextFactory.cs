namespace ProjectBeacon.API.Auth;

using Application.Authorization;
using Microsoft.AspNetCore.Http;

public static class ActorContextHttpExtensions
{
    private const string CacheKey = "ProjectBeacon.ActorContext";

    public static ActorContext GetActor(this HttpContext context)
    {
        if (context.Items.TryGetValue(CacheKey, out var cached) && cached is ActorContext actor)
            return actor;

        var fresh = ActorContextFactory.FromPrincipal(context.User);
        context.Items[CacheKey] = fresh;
        return fresh;
    }
}
