namespace ProjectBeacon.Domain.Enums;

/// <summary>Server-owned lifecycle of the explicitly approved tag/deployment operation.</summary>
public enum ReleasePublicationStatus
{
    NotStarted = 0,
    Queued = 1,
    FailedBeforeTag = 2,
    FailedAfterTag = 3,
    Published = 4
}
