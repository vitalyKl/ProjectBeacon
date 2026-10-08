namespace ProjectBeacon.Application.Authorization;

using Application.Common;
using Domain.Enums;
using Infrastructure.Data;

/// <summary>Authorizes an actor for a resource action, a membership change, token creation, or an API-token capability.</summary>
public interface IAuthorizationService
{
    /// <summary>Whether <paramref name="actor"/> may perform <paramref name="action"/> on <paramref name="resource"/>.</summary>
    Task<Result> CanAsync(
        IBeaconDb db, ActorContext actor,
        ResourceType resource, AuthAction action,
        Guid? projectId = null, Guid? orgId = null,
        CancellationToken ct = default);

    /// <summary>Whether <paramref name="actor"/> may add <paramref name="targetUserId"/> to <paramref name="projectId"/> as <paramref name="targetRole"/>.</summary>
    Task<Result> CanAddMemberAsync(
        IBeaconDb db, ActorContext actor, Guid projectId,
        Guid targetUserId, MemberRole targetRole,
        CancellationToken ct = default);

    /// <summary>Whether <paramref name="actor"/> may remove <paramref name="targetUserId"/> from <paramref name="projectId"/>.</summary>
    Task<Result> CanRemoveMemberAsync(
        IBeaconDb db, ActorContext actor, Guid projectId,
        Guid targetUserId,
        CancellationToken ct = default);

    /// <summary>Whether <paramref name="actor"/> may create a project API token with <paramref name="requestedCapabilities"/>.</summary>
    Task<Result> CanCreateTokenAsync(
        IBeaconDb db, ActorContext actor, Guid projectId,
        ApiTokenCapability requestedCapabilities,
        CancellationToken ct = default);

    /// <summary>Whether <paramref name="actor"/> satisfies <paramref name="required"/>.</summary>
    bool HasCapability(ActorContext actor, ApiTokenCapability required);
}
