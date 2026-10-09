namespace ProjectBeacon.Application.Chat;

using Application.Common;
using Domain.Enums;
/// <summary>
/// A workstation chat session: device, external session id, and status. The local root is never exposed.
/// </summary>
public record ChatSessionDto(
    Guid Id,
    Guid ProjectId,
    Guid DeviceId,
    string ExternalSessionId,
    string Title,
    ChatSessionStatus Status,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
/// <summary>
/// One chat part: role, kind, body, optional external id, and sort order.
/// </summary>
public record ChatPartDto(
    Guid Id,
    Guid SessionId,
    string Role,
    string Kind,
    string Body,
    string? ExternalId,
    int SortOrder,
    DateTime CreatedAt);
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
public record SendChatPromptRequest(Guid SessionId, Guid UserId, string Text, string? Model);
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
/// Device-posted chat part. A repeated ExternalId does not insert a second row.
/// </summary>
public record AppendChatPartRequest(Guid SessionId, Guid DeviceId, string Role, string Kind, string Body, string? ExternalId);
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
