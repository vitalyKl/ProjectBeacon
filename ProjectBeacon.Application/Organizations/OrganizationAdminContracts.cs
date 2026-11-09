namespace ProjectBeacon.Application.Organizations;

using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Domain.Enums;

public sealed record OrganizationAdminListItem(
    Guid Id, string Name, string? Description, DateTime CreatedAt, DateTime? UpdatedAt,
    string? OwnerEmail, int MemberCount, int ProjectCount, int PendingInviteCount);

public sealed record OrganizationAdminPage(
    IReadOnlyList<OrganizationAdminListItem> Items, long TotalCount, int Page, int PageSize);

public sealed record OrganizationAdminMember(
    Guid UserId, string Login, string Email, string Role, bool IsDisabled, bool IsPlatformAdmin, DateTime JoinedAt);

public sealed record OrganizationAdminInvite(
    Guid Id, string Email, string Role, string Status, DateTime CreatedAt, DateTime ExpiresAt);

public sealed record OrganizationAdminDetails(
    OrganizationAdminListItem Organization,
    IReadOnlyList<OrganizationAdminMember> Members,
    IReadOnlyList<OrganizationAdminInvite> Invites,
    int ProjectCount);

public sealed record CreateOrganizationWithOwnerRequest(
    string Name, string? Description, string OwnerEmail);

/// <summary>One-time response. The raw invite token is not persisted or audited.</summary>
public sealed record OperatorInviteCreated(
    Guid OrgId, Guid InviteId, string Email, DateTime ExpiresAtUtc, string InviteUrl);

public interface IOrganizationAdminService
{
    Task<Result<OrganizationAdminPage>> ListAsync(
        AdminActor actor, string? search = null, int page = 1, int pageSize = 25,
        CancellationToken ct = default);

    Task<Result<OrganizationAdminDetails>> GetAsync(
        AdminActor actor, Guid orgId, CancellationToken ct = default);

    Task<Result<OperatorInviteCreated>> CreateAsync(
        AdminActor actor, CreateOrganizationWithOwnerRequest request, CancellationToken ct = default);

    Task<Result<OperatorInviteCreated>> InviteOwnerAsync(
        AdminActor actor, Guid orgId, string email, CancellationToken ct = default);

    Task<Result> RevokeInviteAsync(
        AdminActor actor, Guid orgId, Guid inviteId, CancellationToken ct = default);

    Task<Result> ChangeMemberRoleAsync(
        AdminActor actor, Guid orgId, Guid userId, MemberRole role, CancellationToken ct = default);

    /// <summary>
    /// Enables or disables the global user account, not just access to the supplied organization.
    /// Disabling invalidates every existing customer session and is rejected if it would leave
    /// any organization without an active owner.
    /// </summary>
    Task<Result> SetAccountDisabledAsync(
        AdminActor actor, Guid orgId, Guid userId, bool disabled, CancellationToken ct = default);
}
