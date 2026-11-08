namespace ProjectBeacon.Application.Admin;

using Application.Common;
using Application.Authorization;

/// <summary>
/// Resolves a human operator against current server-side account state and returns a
/// capability-scoped AdminActor. Implementations must reject API tokens, devices and workers.
/// </summary>
public interface IAdminGate
{
    Task<Result<AdminActor>> RequireAsync(
        ActorContext actor,
        OperatorPermission required,
        bool requireElevation = false,
        CancellationToken ct = default);
}