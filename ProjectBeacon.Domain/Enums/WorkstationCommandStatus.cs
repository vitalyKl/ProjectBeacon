namespace ProjectBeacon.Domain.Enums;

/// <summary>Queue state of a command waiting on a device.</summary>
public enum WorkstationCommandStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled
}
