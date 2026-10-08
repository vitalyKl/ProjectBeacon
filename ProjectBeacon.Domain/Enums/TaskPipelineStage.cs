namespace ProjectBeacon.Domain.Enums;

/// <summary>Pipeline position of a task. Separate from <see cref="TaskItemStatus"/>.</summary>
    public enum TaskPipelineStage
{
    None,
    Planning,
    Executing,
    Reviewing,
    Approved,
    ReopenedForRevision,
    Closed
}
