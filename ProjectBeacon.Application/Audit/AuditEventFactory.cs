namespace ProjectBeacon.Application.Audit;

using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Domain.Entities.Audit;

/// <summary>Maps a trusted request actor to the immutable domain audit envelope.</summary>
public static class AuditEventFactory
{
    public static AuditEvent ForActor(
        ActorContext actor,
        string action,
        string targetType,
        string? targetId,
        Guid? orgId,
        Guid? projectId,
        AuditOutcome outcome = AuditOutcome.Success,
        string? detailsJson = null)
    {
        var actorKind = actor.Type switch
        {
            ActorType.Human => AuditActorKind.Human,
            ActorType.ApiToken => AuditActorKind.ApiToken,
            ActorType.Device => AuditActorKind.Device,
            ActorType.Worker => AuditActorKind.Worker,
            _ => throw new ArgumentOutOfRangeException(nameof(actor), "Unknown actor type.")
        };

        return AuditEvent.Create(
            DateTime.UtcNow,
            actorKind,
            actor.UserId,
            actor.TokenId,
            actor.DeviceId,
            action,
            targetType,
            targetId,
            orgId,
            projectId,
            outcome,
            detailsJson: detailsJson);
    }
}
