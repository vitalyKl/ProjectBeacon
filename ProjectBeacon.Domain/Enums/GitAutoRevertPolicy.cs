namespace ProjectBeacon.Domain.Enums;

/// <summary>When ProjectBeacon may automatically revert a landed merge.</summary>
public enum GitAutoRevertPolicy
{
    Off = 0,
    OnPostCheckFail = 1
}
