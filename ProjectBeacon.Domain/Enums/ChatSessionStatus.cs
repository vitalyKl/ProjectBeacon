namespace ProjectBeacon.Domain.Enums;

/// <summary>Workstation chat turn state. Distinct from pipeline <see cref="SessionStatus"/>.</summary>
public enum ChatSessionStatus
{
    Idle,
    Streaming,
    Aborted
}
