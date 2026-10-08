namespace ProjectBeacon.Domain.Enums;

/// <summary>Pipeline session state. A new session is created for each role turn.</summary>
    public enum SessionStatus
{
    Ready,
    Active,
    Closed,
    Failed
}
