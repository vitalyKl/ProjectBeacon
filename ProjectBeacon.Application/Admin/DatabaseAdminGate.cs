namespace ProjectBeacon.Application.Admin;

using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Persistence;

/// <summary>
/// Resolves current platform-operator authority from the persisted account, never from
/// caller-supplied permissions. The only live role in alpha is SystemAdministrator,
/// represented by the existing User.IsAdmin flag.
/// </summary>
public sealed class DatabaseAdminGate(IBeaconDbFactory dbFactory) : IAdminGate
{
    public async Task<Result<AdminActor>> RequireAsync(
        ActorContext actor,
        OperatorPermission required,
        bool requireElevation = false,
        CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is not { } userId || userId == Guid.Empty)
            return Result.Failure<AdminActor>("An authenticated human operator is required.", ErrorKind.Forbidden);

        if (required == OperatorPermission.None
            || (required & ~OperatorPermissionSets.CurrentSystemAdministrator) != 0)
            return Result.Failure<AdminActor>("A valid operator permission is required.", ErrorKind.Validation);

        if (!actor.IsAdmin)
            return Result.Failure<AdminActor>("The account is not a platform operator.", ErrorKind.Forbidden);

        await using var db = dbFactory.CreateDbContext();
        var account = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new OperatorAccountState(user.IsAdmin, user.DisabledAt != null))
            .FirstOrDefaultAsync(ct);

        if (account is null || account.IsDisabled)
            return Result.Failure<AdminActor>("The operator account is not available.", ErrorKind.Unauthorized);

        // The ActorContext is resolved from the authenticated ticket. A mismatch indicates
        // stale or forged authority; do not upgrade it from the database inside this request.
        if (!account.IsAdmin)
            return Result.Failure<AdminActor>("The account is not a platform operator.", ErrorKind.Forbidden);

        var permissions = OperatorRolePermissionSets.For(OperatorRole.SystemAdministrator);
        if (!OperatorPermissionSets.Includes(permissions, required))
            return Result.Failure<AdminActor>("The operator does not have the required permission.", ErrorKind.Forbidden);

        // Step-up authentication is deliberately fail-closed until an elevation provider
        // is implemented. No endpoint may silently treat a normal login as elevated.
        if (requireElevation)
            return Result.Failure<AdminActor>("Operator step-up authentication is not configured.", ErrorKind.Forbidden);

        return Result.Ok(new AdminActor(userId, permissions, elevatedUntilUtc: null));
    }

    private sealed record OperatorAccountState(bool IsAdmin, bool IsDisabled);
}
