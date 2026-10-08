namespace ProjectBeacon.Application.Identity;

using Application.Authorization;
using Domain.Enums;
/// <summary>
/// Org, email, role, and the caller creating the invite.
/// </summary>
public record CreateOrgInviteRequest(Guid OrgId, string Email, MemberRole Role, ActorContext Actor);
/// <summary>
/// Project, email, role, and the caller creating the invite.
/// </summary>
public record CreateProjectInviteRequest(Guid ProjectId, string Email, MemberRole Role, ActorContext Actor);
/// <summary>
/// A new invite. Token is the raw value, shown once. Url is the accept link.
/// </summary>
public record InviteCreatedDto(
    Guid Id,
    string Email,
    MemberRole Role,
    DateTime ExpiredAt,
    string Token,
    string Url);
/// <summary>
/// Invite shown before the caller is a member: kind, target, email, role, and expiry.
/// </summary>
public record InvitePreviewDto(
    string Kind,
    Guid TargetId,
    string TargetName,
    string Email,
    MemberRole Role,
    DateTime ExpiredAt);
/// <summary>
/// Raw invite token and the signed-in caller. The email must match.
/// </summary>
public record AcceptInviteRequest(string Token, ActorContext Actor);
/// <summary>
/// Fields for list invites.
/// </summary>
public record ListInvitesRequest(Guid TargetId, ActorContext Actor);
/// <summary>
/// An invite row without the raw token.
/// </summary>
public record InviteListDto(
    Guid Id,
    string Email,
    MemberRole Role,
    string Status,
    DateTime ExpiredAt,
    DateTime CreatedAt);
/// <summary>
/// Invite to revoke and the caller. The caller must be allowed to administer the target.
/// </summary>
public record RevokeInviteRequest(Guid InviteId, ActorContext Actor);
