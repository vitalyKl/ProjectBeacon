namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class TaskDependency : Entity
{
    public TaskDependency() { }

    public Guid TaskId { get; private set; }
    public Guid DependentTaskId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public TaskItem Task { get; private set; } = null!;
    public TaskItem DependentTask { get; private set; } = null!;

    public static TaskDependency Create(Guid taskId, Guid dependentTaskId)
    {
        var dependency = Entity.New<TaskDependency>();
        dependency.TaskId = taskId;
        dependency.DependentTaskId = dependentTaskId;
        dependency.CreatedAt = DateTime.UtcNow;
        return dependency;
    }
}
