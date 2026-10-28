namespace ProjectBeacon.Application.Chat;

using Application.Common;
using Domain.Enums;

/// <summary>
/// A workstation chat session: device, external session id, and status. The local root is never exposed.
/// </summary>
public record ChatSessionDto(
    Guid Id,
    Guid ProjectId,
    string? ProjectName,
    Guid DeviceId,
    string ExternalSessionId,
    string Title,
    ChatSessionStatus Status,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? AssistantMessages = null,
    double? TotalCost = null,
    int? ContextLimit = null);

/// <summary>
/// One chat part: role, kind, body, optional external id, and sort order.
/// Structured tool fields are null for plain text parts and legacy rows.
/// </summary>
public record ChatPartDto(
    Guid Id,
    Guid SessionId,
    string Role,
    string Kind,
    string Body,
    string? ExternalId,
    int SortOrder,
    DateTime CreatedAt,
    string? Tool = null,
    string? ToolStatus = null,
    string? ToolInput = null,
    string? ToolResult = null,
    int? DurationMs = null,
    bool Complete = false);

/// <summary>
/// User and optional title for a new chat on the current project device.
/// </summary>
public record CreateChatSessionRequest(Guid UserId, string? Title);

/// <summary>
/// Command for create chat session.
/// </summary>
public record CreateChatSessionCommand(CreateChatSessionRequest Request) : ICommand<Result<ChatSessionDto>>;

/// <summary>
/// Fields for list chat sessions.
/// </summary>
public record ListChatSessionsRequest(Guid UserId);

/// <summary>
/// Command for list chat sessions.
/// </summary>
public record ListChatSessionsCommand(ListChatSessionsRequest Request) : ICommand<Result<IList<ChatSessionDto>>>;

/// <summary>
/// Fields for get chat session.
/// </summary>
public record GetChatSessionRequest(Guid SessionId, Guid UserId);

/// <summary>
/// Command for get chat session.
/// </summary>
public record GetChatSessionCommand(GetChatSessionRequest Request) : ICommand<Result<ChatSessionDto>>;

/// <summary>
/// Fields for list chat parts.
/// </summary>
public record ListChatPartsRequest(Guid SessionId, Guid UserId);

/// <summary>
/// Command for list chat parts.
/// </summary>
public record ListChatPartsCommand(ListChatPartsRequest Request) : ICommand<Result<IList<ChatPartDto>>>;

/// <summary>
/// Session, user, message text, and an optional model id.
/// </summary>
public record SendChatPromptRequest(Guid SessionId, Guid UserId, string Text, string? Model, string? ReasoningEffort = null);

/// <summary>
/// Command for send chat prompt.
/// </summary>
public record SendChatPromptCommand(SendChatPromptRequest Request) : ICommand<Result<ChatSessionDto>>;

/// <summary>
/// User aborting a chat session.
/// </summary>
public record AbortChatRequest(Guid SessionId, Guid UserId);

/// <summary>
/// Command for abort chat.
/// </summary>
public record AbortChatCommand(AbortChatRequest Request) : ICommand<Result<ChatSessionDto>>;

/// <summary>
/// User switching a chat session's agent (e.g., plan/act).
/// </summary>
public record SwitchChatAgentRequest(Guid SessionId, Guid UserId, string Agent);

/// <summary>
/// Command for switch chat agent.
/// </summary>
public record SwitchChatAgentCommand(SwitchChatAgentRequest Request) : ICommand<Result<ChatSessionDto>>;

/// <summary>
/// User answering a model question (form) surfaced in a chat session.
/// </summary>
public record ReplyChatFormRequest(Guid SessionId, Guid UserId, string FormId, string AnswerJson);

/// <summary>
/// Command for reply chat form.
/// </summary>
public record ReplyChatFormCommand(ReplyChatFormRequest Request) : ICommand<Result<ChatSessionDto>>;

/// <summary>
/// User renaming a chat session.
/// </summary>
public record RenameChatSessionRequest(Guid SessionId, Guid UserId, string Title);

/// <summary>
/// Command for rename chat session.
/// </summary>
public record RenameChatSessionCommand(RenameChatSessionRequest Request) : ICommand<Result<ChatSessionDto>>;

/// <summary>
/// User archiving (soft-deleting) a chat session. An active session is aborted first.
/// </summary>
public record ArchiveChatSessionRequest(Guid SessionId, Guid UserId);

/// <summary>
/// Command for archive chat session.
/// </summary>
public record ArchiveChatSessionCommand(ArchiveChatSessionRequest Request) : ICommand<Result<ChatSessionDto>>;

/// <summary>
/// Device-posted chat part. A repeated ExternalId does not insert a second row.
/// Structured tool fields describe tool call parts.
/// </summary>
public record AppendChatPartRequest(
    Guid SessionId,
    Guid DeviceId,
    string Role,
    string Kind,
    string Body,
    string? ExternalId,
    string? Tool = null,
    string? ToolStatus = null,
    string? ToolInput = null,
    string? ToolResult = null,
    int? DurationMs = null,
    bool Complete = false);

/// <summary>
/// Command for append chat part.
/// </summary>
public record AppendChatPartCommand(AppendChatPartRequest Request) : ICommand<Result<ChatPartDto>>;

/// <summary>
/// Device marking its chat session idle.
/// </summary>
public record MarkChatIdleRequest(Guid SessionId, Guid DeviceId);

/// <summary>
/// Command for mark chat idle.
/// </summary>
public record MarkChatIdleCommand(MarkChatIdleRequest Request) : ICommand<Result<ChatSessionDto>>;

/// <summary>
/// A device reports the session's token totals and the active model's context limit.
/// </summary>
public record ReportChatUsageRequest(
    Guid SessionId,
    Guid DeviceId,
    int PromptTokens,
    int CompletionTokens,
    int AssistantMessages,
    double TotalCost,
    int? ContextLimit = null);

/// <summary>
/// Command for report chat usage.
/// </summary>
public record ReportChatUsageCommand(ReportChatUsageRequest Request) : ICommand<Result<ChatSessionDto>>;
