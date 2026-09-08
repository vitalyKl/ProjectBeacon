namespace ProjectBeacon.Domain.Entities;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class TaskItem : Entity
{
    public TaskItem() { }

    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public TaskItemStatus Status { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid? LabelId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    public Project Project { get; private set; } = null!;
    public Label? Label { get; private set; }

    public static TaskItem Create(string title, Guid projectId)
    {
        var task = Entity.New<TaskItem>();
        task.Title = title;
        task.ProjectId = projectId;
        task.Status = TaskItemStatus.Todo;
        task.CreatedAt = DateTime.UtcNow;
        return task;
    }

    public void AssignLabel(Guid labelId)
    {
        LabelId = labelId;
    }

    public void MoveToNextStatus()
    {
        Status = Status switch
        {
            TaskItemStatus.Todo => TaskItemStatus.InProgress,
            TaskItemStatus.InProgress => TaskItemStatus.Done,
            TaskItemStatus.Done => TaskItemStatus.Done,
            _ => throw new InvalidOperationException($"Unknown status: {Status}")
        };

        if (Status == TaskItemStatus.Done)
            CompletedAt = DateTime.UtcNow;
    }
}
