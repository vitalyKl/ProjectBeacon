namespace ProjectBeacon.Domain.Entities.Chat;

using ProjectBeacon.Domain.Common;

/// <summary>One message or tool part in a <see cref="ChatSession"/>.</summary>
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

    /// <summary>Structured tool call fields. Null for plain text parts and for legacy rows.</summary>
    public string? Tool { get; private set; }
    public string? ToolStatus { get; private set; }
    public string? ToolInput { get; private set; }
    public string? ToolResult { get; private set; }
    public int? DurationMs { get; private set; }

    /// <summary>True once the part content is final. Streamed snapshots arrive with false until the source message completes.</summary>
    public bool Complete { get; private set; }

    public ChatSession Session { get; private set; } = null!;

    public static ChatPart Create(
        Guid sessionId,
        Guid projectId,
        string role,
        string kind,
        string body,
        int sortOrder,
        string? externalId = null,
        string? tool = null,
        string? toolStatus = null,
        string? toolInput = null,
        string? toolResult = null,
        int? durationMs = null,
        bool complete = false)
    {
        var part = Entity.New<ChatPart>();
        part.SessionId = sessionId;
        part.ProjectId = projectId;
        part.Role = string.IsNullOrWhiteSpace(role) ? "assistant" : role.Trim();
        part.Kind = string.IsNullOrWhiteSpace(kind) ? "text" : kind.Trim();
        part.Body = body ?? "";
        part.SortOrder = sortOrder;
        part.ExternalId = string.IsNullOrWhiteSpace(externalId) ? null : externalId.Trim();
        part.Tool = TrimTo(tool, 40);
        part.ToolStatus = TrimTo(toolStatus, 40);
        part.ToolInput = string.IsNullOrWhiteSpace(toolInput) ? null : toolInput;
        part.ToolResult = string.IsNullOrWhiteSpace(toolResult) ? null : toolResult;
        part.DurationMs = durationMs is > 0 ? durationMs : null;
        part.Complete = complete;
        part.CreatedAt = DateTime.UtcNow;
        return part;
    }

    /// <summary>Overwrites an existing part's content with a newer snapshot of the same external id.</summary>
    public void Update(
        string kind,
        string body,
        string? tool,
        string? toolStatus,
        string? toolInput,
        string? toolResult,
        int? durationMs,
        bool complete)
    {
        Kind = string.IsNullOrWhiteSpace(kind) ? "text" : kind.Trim();
        Body = body ?? "";
        Tool = TrimTo(tool, 40);
        ToolStatus = TrimTo(toolStatus, 40);
        ToolInput = string.IsNullOrWhiteSpace(toolInput) ? null : toolInput;
        ToolResult = string.IsNullOrWhiteSpace(toolResult) ? null : toolResult;
        DurationMs = durationMs is > 0 ? durationMs : null;
        Complete = complete;
    }

    private static string? TrimTo(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max);
    }
}
