namespace ProjectBeacon.Application.Projects;

using System.Text.Json.Serialization;
using Application.Authorization;
using Application.Common;
using Domain.Enums;

public record CreateApiTokenRequest(Guid ProjectId, string Name, ApiTokenCapability Capabilities, DateTime? ExpiresAt);

public record CreateApiTokenCommand(CreateApiTokenRequest Request, ActorContext Actor) : ICommand<Result<ApiTokenDto>>;

public record RevokeApiTokenRequest(Guid ProjectId, Guid TokenId);

public record RevokeApiTokenCommand(RevokeApiTokenRequest Request, ActorContext Actor) : ICommand<Result>;

public record GetApiTokenRequest(Guid ProjectId, Guid TokenId);

public record GetApiTokenCommand(GetApiTokenRequest Request, ActorContext Actor) : ICommand<Result<ApiTokenDto>>;

public record ListApiTokensRequest(Guid ProjectId);

public record ListApiTokensCommand(ListApiTokensRequest Request, ActorContext Actor) : ICommand<Result<IList<ApiTokenDto>>>;

public record ApiTokenDto(
    Guid Id,
    string Name,
    string? TokenPrefix,
    Guid ProjectId,
    ApiTokenCapability Capabilities,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    DateTime CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Token = null);
