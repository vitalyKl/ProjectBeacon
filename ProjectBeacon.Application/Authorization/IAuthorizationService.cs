namespace ProjectBeacon.Application.Authorization;

using Application.Common;
using Domain.Enums;
using Infrastructure.Data;

public interface IAuthorizationService
{
    Task<Result> CanAsync(
        IBeaconDb db, ActorContext actor,
        ResourceType resource, AuthAction action,
        Guid? projectId = null, Guid? orgId = null,
        CancellationToken ct = default);

    Task<Result> CanAddMemberAsync(
        IBeaconDb db, ActorContext actor, Guid projectId,
        Guid targetUserId, MemberRole targetRole,
        CancellationToken ct = default);

    Task<Result> CanRemoveMemberAsync(
        IBeaconDb db, ActorContext actor, Guid projectId,
        Guid targetUserId,
        CancellationToken ct = default);

    Task<Result> CanCreateTokenAsync(
        IBeaconDb db, ActorContext actor, Guid projectId,
        ApiTokenCapability requestedCapabilities,
        CancellationToken ct = default);

    bool HasCapability(ActorContext actor, ApiTokenCapability required);
}
