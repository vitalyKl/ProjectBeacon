namespace ProjectBeacon.Domain.Enums;

/// <summary>Handling for a dirty checkout of the target branch.</summary>
public enum GitDirtyCheckoutPolicy
{
    Escalate = 0,
    LandViaIntegration = 1
}
