namespace ProjectBeacon.Domain.Enums;

/// <summary>Lifecycle of one verified attempt to merge a task branch.</summary>
public enum LandingAttemptStatus
{
    PreviewPending,
    Previewed,
    Conflicted,
    Failed,
    ExecutePending,
    Landed,
    RevertPending,
    Reverted,
    RevertFailed
}
