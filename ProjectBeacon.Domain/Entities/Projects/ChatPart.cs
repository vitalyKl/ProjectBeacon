namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class ChatPart : Entity, IProjectScoped
{
    public ChatPart() { }

    public Guid SessionId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Role { get; private set; } = "assistant";
    public string Kind { get; private set; } = "text";
    public string Body { get; private set; } = "";
    public string? ExternalId { get; private set; }
    public int SortOrder { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public ChatSession Session { get; private set; } = null!;

    public static ChatPart Create(
        Guid sessionId, Guid projectId, string role, string kind, string body, int sortOrder, string? externalId = null)
    {
        var part = Entity.New<ChatPart>();
        part.SessionId = sessionId;
        part.ProjectId = projectId;
        part.Role = string.IsNullOrWhiteSpace(role) ? "assistant" : role.Trim();
        part.Kind = string.IsNullOrWhiteSpace(kind) ? "text" : kind.Trim();
        part.Body = body ?? "";
        part.SortOrder = sortOrder;
        part.ExternalId = string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim();
        part.CreatedAt = DateTime.UtcNow;
        return part;
    }
}
