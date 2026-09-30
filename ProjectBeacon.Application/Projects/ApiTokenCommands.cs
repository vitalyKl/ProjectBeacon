namespace ProjectBeacon.Application.Projects;

using Application.Common;
using Domain.Enums;

public record CreateApiTokenRequest(Guid ProjectId, string Name, ApiTokenCapability Capabilities, DateTime? ExpiresAt, Guid? CreatedByUserId);

public record CreateApiTokenCommand(CreateApiTokenRequest Request, Guid ActorUserId, bool ActorIsAdmin, bool ActorIsApiToken) : ICommand<Result<ApiTokenDto>>;

public record RevokeApiTokenRequest(Guid ProjectId, Guid TokenId);

public record RevokeApiTokenCommand(RevokeApiTokenRequest Request, Guid ActorUserId, bool ActorIsAdmin, bool ActorIsApiToken) : ICommand<Result>;

public record GetApiTokenRequest(Guid TokenId);

public record GetApiTokenCommand(GetApiTokenRequest Request) : ICommand<Result<ApiTokenDto>>;

public record ListApiTokensRequest(Guid ProjectId);

public record ListApiTokensCommand(ListApiTokensRequest Request) : ICommand<Result<IList<ApiTokenDto>>>;

public record ApiTokenDto(
    Guid Id,
    string Name,
    string? TokenPrefix,
    Guid ProjectId,
    ApiTokenCapability Capabilities,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    DateTime CreatedAt);
