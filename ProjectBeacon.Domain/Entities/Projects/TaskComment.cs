namespace ProjectBeacon.Domain.Entities.Projects;

using Identity;
using ProjectBeacon.Domain.Common;

public class TaskComment : Entity
{
    public TaskComment() { }

    public string Content { get; private set; } = string.Empty;
    public Guid TaskId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public TaskItem Task { get; private set; } = null!;
    public User User { get; private set; } = null!;

    public static TaskComment Create(string content, Guid taskId, Guid userId)
    {
        var comment = Entity.New<TaskComment>();
        comment.Content = content;
        comment.TaskId = taskId;
        comment.UserId = userId;
        comment.CreatedAt = DateTime.UtcNow;
        return comment;
    }

    public void Update(string content)
    {
        Content = content;
        UpdatedAt = DateTime.UtcNow;
    }
}
