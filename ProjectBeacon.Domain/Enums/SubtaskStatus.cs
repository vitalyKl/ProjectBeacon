namespace ProjectBeacon.Domain.Enums;

/// <summary>Actor subtask state. <see cref="Failed"/> is terminal until the pipeline reopens it.</summary>
public enum SubtaskStatus
{
    Pending,
    InProgress,
    Done,
    Failed
}
