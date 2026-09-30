namespace ProjectBeacon.Application.Authorization;

public readonly record struct ActorContext(Guid UserId, bool IsAdmin, bool IsApiToken)
{
    public static readonly ActorContext Anonymous = new(Guid.Empty, false, false);
}
