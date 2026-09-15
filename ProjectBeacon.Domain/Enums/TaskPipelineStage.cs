namespace ProjectBeacon.Domain.Enums;

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
