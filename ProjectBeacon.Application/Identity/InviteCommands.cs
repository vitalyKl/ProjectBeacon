namespace ProjectBeacon.Application.Identity;

using Application.Authorization;
using Domain.Enums;

public record CreateOrgInviteRequest(Guid OrgId, string Email, MemberRole Role, ActorContext Actor);

public record CreateProjectInviteRequest(Guid ProjectId, string Email, MemberRole Role, ActorContext Actor);

public record InviteCreatedDto(
    Guid Id,
    string Email,
    MemberRole Role,
    DateTime ExpiredAt,
    string Token,
    string Url);

public record InvitePreviewDto(
    string Kind,
    Guid TargetId,
    string TargetName,
    string Email,
    MemberRole Role,
    DateTime ExpiredAt);

public record AcceptInviteRequest(string Token, ActorContext Actor);

public record ListInvitesRequest(Guid TargetId, ActorContext Actor);

public record InviteListDto(
    Guid Id,
    string Email,
    MemberRole Role,
    string Status,
    DateTime ExpiredAt,
    DateTime CreatedAt);

public record RevokeInviteRequest(Guid InviteId, ActorContext Actor);
