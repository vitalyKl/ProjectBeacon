namespace ProjectBeacon.Application.Projects;

using System.Text.Json.Serialization;
using Application.Authorization;
using Application.Common;
using Domain.Enums;
/// <summary>
/// Project, name, capability flags, and optional expiry. The raw token is not on this request.
/// </summary>
public record CreateApiTokenRequest(Guid ProjectId, string Name, ApiTokenCapability Capabilities, DateTime? ExpiresAt);
/// <summary>
/// Command for create api token.
/// </summary>
public record CreateApiTokenCommand(CreateApiTokenRequest Request, ActorContext Actor) : ICommand<Result<ApiTokenDto>>;
/// <summary>
/// Fields for revoke api token.
/// </summary>
public record RevokeApiTokenRequest(Guid ProjectId, Guid TokenId);
/// <summary>
/// Command for revoke api token.
/// </summary>
public record RevokeApiTokenCommand(RevokeApiTokenRequest Request, ActorContext Actor) : ICommand<Result>;
/// <summary>
/// Fields for get api token.
/// </summary>
public record GetApiTokenRequest(Guid ProjectId, Guid TokenId);
/// <summary>
/// Command for get api token.
/// </summary>
public record GetApiTokenCommand(GetApiTokenRequest Request, ActorContext Actor) : ICommand<Result<ApiTokenDto>>;
/// <summary>
/// Fields for list api tokens.
/// </summary>
public record ListApiTokensRequest(Guid ProjectId);
/// <summary>
/// Command for list api tokens.
/// </summary>
public record ListApiTokensCommand(ListApiTokensRequest Request, ActorContext Actor) : ICommand<Result<IList<ApiTokenDto>>>;
/// <summary>
/// Token metadata. Token is the raw secret only when just created, and is omitted from JSON when null.
/// </summary>
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
