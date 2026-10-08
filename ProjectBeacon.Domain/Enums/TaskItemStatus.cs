namespace ProjectBeacon.Domain.Enums;

/// <summary>
    /// Board status of a <c>TaskItem</c>. Named to avoid BCL <c>TaskStatus</c>.
    /// Moving <see cref="InProgress"/> to <see cref="Done"/> requires review notes.
    /// </summary>
    public enum TaskItemStatus
{
    Todo,
    InProgress,
    Done
}
