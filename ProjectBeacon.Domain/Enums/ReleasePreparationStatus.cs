namespace ProjectBeacon.Domain.Enums;

/// <summary>Server-owned state of the two-step ReleasePrepare workstation workflow.</summary>
public enum ReleasePreparationStatus
{
    NotStarted = 0,
    PrepareQueued = 1,
    Prepared = 2,
    VerifyQueued = 3,
    VerifyFailed = 4,
    Failed = 5,
    Candidate = 6
}
