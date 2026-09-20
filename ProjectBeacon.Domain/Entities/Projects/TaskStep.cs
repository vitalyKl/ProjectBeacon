namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class TaskStep : Entity, IProjectScoped
{
    public TaskStep() { }

    public Guid TaskId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? DoneAt { get; private set; }

    public bool IsDone => DoneAt is not null;

    public static TaskStep Create(Guid taskId, Guid projectId, string title, int sortOrder)
    {
        var step = Entity.New<TaskStep>();
        step.TaskId = taskId;
        step.ProjectId = projectId;
        step.Title = title.Trim();
        step.SortOrder = sortOrder;
        step.CreatedAt = DateTime.UtcNow;
        return step;
    }

    public void SetDone(bool done) =>
        DoneAt = done ? DateTime.UtcNow : null;
}
