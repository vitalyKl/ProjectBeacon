namespace ProjectBeacon.Domain.Enums;

/// <summary>Actor subtask state. WaitingForUser is resumable; Done and Failed are terminal until reopened.</summary>
public enum SubtaskStatus
{
    Pending,
    InProgress,
    Done,
    Failed,
    WaitingForUser
}
