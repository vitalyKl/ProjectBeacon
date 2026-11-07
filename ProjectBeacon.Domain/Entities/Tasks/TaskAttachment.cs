namespace ProjectBeacon.Domain.Entities.Tasks;

using ProjectBeacon.Domain.Common;

/// <summary>File uploaded by a user or agent and attached to a task.</summary>
public sealed class TaskAttachment : Entity, IProjectScoped
{
    public TaskAttachment() { }

    public Guid ProjectId { get; private set; }
    public Guid TaskId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = "application/octet-stream";
    public long Length { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string CreatedByType { get; private set; } = "Human";
    public Guid? CreatedById { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static TaskAttachment Create(
        Guid projectId, Guid taskId, string fileName, string contentType,
        long length, string storageKey, string createdByType, Guid? createdById)
    {
        var item = Entity.New<TaskAttachment>();
        item.ProjectId = projectId;
        item.TaskId = taskId;
        item.FileName = fileName;
        item.ContentType = contentType;
        item.Length = length;
        item.StorageKey = storageKey;
        item.CreatedByType = createdByType;
        item.CreatedById = createdById;
        item.CreatedAt = DateTime.UtcNow;
        return item;
    }
}
