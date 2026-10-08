namespace ProjectBeacon.Domain.Enums;

/// <summary>Kind of board task. Wire values are these names. Default on create is <see cref="Task"/>.</summary>
public enum TaskType
{
    Feature,
    Bug,
    Improvement,
    Task
}
