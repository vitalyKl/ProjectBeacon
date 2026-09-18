namespace ProjectBeacon.Application.Identity;

using Domain.Enums;

public record CreateOrgInviteRequest(Guid OrgId, string Email, MemberRole Role, Guid ActorUserId, bool ActorIsAdmin);

public record CreateProjectInviteRequest(Guid ProjectId, string Email, MemberRole Role, Guid ActorUserId, bool ActorIsAdmin);

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

public record AcceptInviteRequest(string Token, Guid ActorUserId);

public record ListInvitesRequest(Guid TargetId, Guid ActorUserId, bool ActorIsAdmin);

public record InviteListDto(
    Guid Id,
    string Email,
    MemberRole Role,
    string Status,
    DateTime ExpiredAt,
    DateTime CreatedAt);

public record RevokeInviteRequest(Guid InviteId, Guid ActorUserId, bool ActorIsAdmin);
