namespace ProjectBeacon.Domain.Entities.Tasks;

using ProjectBeacon.Domain.Common;

public class TaskWorkspaceSection : Entity, IProjectScoped
{
    public TaskWorkspaceSection() { }

    public Guid TaskId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Abstract { get; private set; } = string.Empty;
    public string Kind { get; private set; } = "Notes";
    public string Content { get; private set; } = string.Empty;
    public string AuthorType { get; private set; } = "Human";
    public string? AuthorId { get; private set; }
    public string? AuthorRole { get; private set; }
    public string? AuthorProfileKey { get; private set; }
    public Guid? AuthorSubtaskId { get; private set; }
    public Guid? AuthorSessionId { get; private set; }
    public int Revision { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public TaskItem Task { get; private set; } = null!;

    public static TaskWorkspaceSection Create(
        Guid taskId,
        Guid projectId,
        string key,
        string title,
        string abstractText,
        string kind,
        string content,
        string authorType,
        string? authorId,
        string? authorRole = null,
        string? authorProfileKey = null,
        Guid? authorSubtaskId = null,
        Guid? authorSessionId = null)
    {
        var section = Entity.New<TaskWorkspaceSection>();
        section.TaskId = taskId;
        section.ProjectId = projectId;
        section.Key = key;
        section.Title = title;
        section.Abstract = abstractText;
        section.Kind = kind;
        section.Content = content;
        section.AuthorType = authorType;
        section.AuthorId = authorId;
        section.AuthorRole = authorRole;
        section.AuthorProfileKey = authorProfileKey;
        section.AuthorSubtaskId = authorSubtaskId;
        section.AuthorSessionId = authorSessionId;
        section.Revision = 1;
        section.CreatedAt = DateTime.UtcNow;
        section.UpdatedAt = section.CreatedAt;
        return section;
    }
}
