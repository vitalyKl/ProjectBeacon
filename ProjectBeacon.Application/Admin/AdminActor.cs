namespace ProjectBeacon.Application.Admin;

/// <summary>
/// A verified operator identity. Its constructor is internal so callers must obtain it
/// through IAdminGate rather than promote an ActorContext or a client-supplied claim.
/// </summary>
public sealed class AdminActor
{
    internal AdminActor(Guid userId, OperatorPermission permissions, DateTimeOffset? elevatedUntilUtc)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("An operator user id is required.", nameof(userId));

        UserId = userId;
        Permissions = permissions;
        ElevatedUntilUtc = elevatedUntilUtc;
    }

    public Guid UserId { get; }
    public OperatorPermission Permissions { get; }
    public DateTimeOffset? ElevatedUntilUtc { get; }

    public bool HasPermission(OperatorPermission required) =>
        OperatorPermissionSets.Includes(Permissions, required);

    public bool IsElevatedAt(DateTimeOffset utcNow) =>
        ElevatedUntilUtc is { } until && utcNow < until;
}