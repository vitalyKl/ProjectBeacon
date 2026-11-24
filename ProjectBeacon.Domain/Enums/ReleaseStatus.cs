namespace ProjectBeacon.Domain.Enums;

/// <summary>Lifecycle of a project Release. Published is only set by the authorized publication workflow.</summary>
public enum ReleaseStatus
{
    Planned = 0,
    InProgress = 1,
    Candidate = 2,
    Published = 3,
    Cancelled = 4
}
