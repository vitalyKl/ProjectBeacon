namespace ProjectBeacon.Application.Persistence;

using Application.Authorization;
using Domain.Enums;

/// <summary>Maps trusted request identity to immutable content-history attribution.</summary>
public static class ContentAttribution
{
    public static (string? Type, string? Id) FromActor(ActorContext? actor, string? agentId = null)
    {
        if (actor is null)
            return (null, null);

        var value = actor.Value;
        if (value.IsApiToken && !string.IsNullOrWhiteSpace(agentId))
            return ("Agent", agentId);

        return value.Type switch
        {
            ActorType.Human => ("Human", value.UserId?.ToString("D")),
            ActorType.ApiToken => ("ApiToken", value.TokenId?.ToString("D")),
            ActorType.Device => ("Device", value.DeviceId?.ToString("D")),
            ActorType.Worker => ("Worker", value.DeviceId?.ToString("D") ?? value.UserId?.ToString("D")),
            _ => (null, null)
        };
    }
}
