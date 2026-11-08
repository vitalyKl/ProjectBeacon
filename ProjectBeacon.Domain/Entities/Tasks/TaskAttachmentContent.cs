namespace ProjectBeacon.Domain.Entities.Tasks;

using ProjectBeacon.Domain.Common;

/// <summary>Binary payload for a task attachment, kept separate from its listable metadata.</summary>
public sealed class TaskAttachmentContent : Entity, IProjectScoped
{
    public TaskAttachmentContent() { }

    public Guid ProjectId { get; private set; }
    public Guid TaskAttachmentId { get; private set; }
    public byte[] Content { get; private set; } = [];

    public static TaskAttachmentContent Create(Guid projectId, Guid taskAttachmentId, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var item = Entity.New<TaskAttachmentContent>();
        item.ProjectId = projectId;
        item.TaskAttachmentId = taskAttachmentId;
        item.Content = content;
        return item;
    }
}
