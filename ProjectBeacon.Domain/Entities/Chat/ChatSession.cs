namespace ProjectBeacon.Domain.Entities.Chat;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>Workstation chat for a project on one device. Created Idle. This is not a pipeline session.</summary>
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
    public DateTime? ArchivedAt { get; private set; }

    public int? UsagePromptTokens { get; private set; }
    public int? UsageCompletionTokens { get; private set; }
    public int? UsageAssistantMessages { get; private set; }
    public double? UsageTotalCost { get; private set; }
    public int? UsageContextLimit { get; private set; }

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

    /// <summary>Records the session's token totals and the active model's context limit. Nulls stay until first report.</summary>
    public void SetUsage(int promptTokens, int completionTokens, int assistantMessages, double totalCost, int? contextLimit)
    {
        UsagePromptTokens = promptTokens;
        UsageCompletionTokens = completionTokens;
        UsageAssistantMessages = assistantMessages;
        UsageTotalCost = totalCost;
        UsageContextLimit = contextLimit;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Abort()
    {
        Status = ChatSessionStatus.Aborted;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Soft delete: excluded from lists, parts kept for audit. The caller must abort a streaming session first.</summary>
    public void Archive()
    {
        Status = ChatSessionStatus.Idle;
        ArchivedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
