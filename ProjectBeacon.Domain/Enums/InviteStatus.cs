namespace ProjectBeacon.Domain.Enums;

/// <summary>Org or project invite. Only <see cref="Pending"/> invites can be accepted. The worker expires them.</summary>
    public enum InviteStatus
{
    Pending = 0,
    Accepted = 1,
    Expired = 2,
    Revoked = 3
}
