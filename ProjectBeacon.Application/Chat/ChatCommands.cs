namespace ProjectBeacon.Application.Chat;

using Application.Common;
using Domain.Enums;

public record ChatSessionDto(
    Guid Id,
    Guid ProjectId,
    Guid DeviceId,
    string ExternalSessionId,
    string Title,
    string LocalRoot,
    ChatSessionStatus Status,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record ChatPartDto(
    Guid Id,
    Guid SessionId,
    string Role,
    string Kind,
    string Body,
    string? ExternalId,
    int SortOrder,
    DateTime CreatedAt);

public record CreateChatSessionRequest(Guid UserId, string? Title);
public record CreateChatSessionCommand(CreateChatSessionRequest Request) : ICommand<Result<ChatSessionDto>>;

public record ListChatSessionsRequest(Guid UserId);
public record ListChatSessionsCommand(ListChatSessionsRequest Request) : ICommand<Result<IList<ChatSessionDto>>>;

public record GetChatSessionRequest(Guid SessionId, Guid UserId);
public record GetChatSessionCommand(GetChatSessionRequest Request) : ICommand<Result<ChatSessionDto>>;

public record ListChatPartsRequest(Guid SessionId, Guid UserId);
public record ListChatPartsCommand(ListChatPartsRequest Request) : ICommand<Result<IList<ChatPartDto>>>;

public record SendChatPromptRequest(Guid SessionId, Guid UserId, string Text, string? Model);
public record SendChatPromptCommand(SendChatPromptRequest Request) : ICommand<Result<ChatSessionDto>>;

public record AbortChatRequest(Guid SessionId, Guid UserId);
public record AbortChatCommand(AbortChatRequest Request) : ICommand<Result<ChatSessionDto>>;

public record AppendChatPartRequest(Guid SessionId, Guid DeviceId, string Role, string Kind, string Body, string? ExternalId);
public record AppendChatPartCommand(AppendChatPartRequest Request) : ICommand<Result<ChatPartDto>>;

public record MarkChatIdleRequest(Guid SessionId, Guid DeviceId);
public record MarkChatIdleCommand(MarkChatIdleRequest Request) : ICommand<Result<ChatSessionDto>>;
