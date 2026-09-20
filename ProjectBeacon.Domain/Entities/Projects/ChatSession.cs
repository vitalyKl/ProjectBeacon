namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class ChatSession : Entity, IProjectScoped
{
    public ChatSession() { }

    public Guid ProjectId { get; private set; }
    public Guid DeviceId { get; private set; }
    public string ExternalSessionId { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string LocalRoot { get; private set; } = "";
    public ChatSessionStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public ICollection<ChatPart> Parts { get; private set; } = [];

    public static ChatSession Create(Guid projectId, Guid deviceId, string title, string localRoot)
    {
        var session = Entity.New<ChatSession>();
        session.ProjectId = projectId;
        session.DeviceId = deviceId;
        session.Title = string.IsNullOrWhiteSpace(title) ? "Chat" : title.Trim();
        session.LocalRoot = localRoot;
        session.Status = ChatSessionStatus.Idle;
        session.CreatedAt = DateTime.UtcNow;
        return session;
    }

    public void SetExternalSessionId(string id)
    {
        ExternalSessionId = id;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetTitle(string title)
    {
        if (!string.IsNullOrWhiteSpace(title))
            Title = title.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetStreaming()
    {
        Status = ChatSessionStatus.Streaming;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetIdle()
    {
        Status = ChatSessionStatus.Idle;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Abort()
    {
        Status = ChatSessionStatus.Aborted;
        UpdatedAt = DateTime.UtcNow;
    }
}
