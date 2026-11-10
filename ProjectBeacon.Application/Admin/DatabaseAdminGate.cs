namespace ProjectBeacon.Application.Admin;

using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Persistence;

/// <summary>
/// Resolves current platform-operator authority from the persisted role assignment, never from
/// caller-supplied permissions. User.IsAdmin remains true only for SystemAdministrator
/// to preserve existing customer-side authorization semantics.
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
            .Select(user => new OperatorAccountState(user.IsAdmin, user.DisabledAt != null, user.TotpEnabled, user.OperatorRole))
            .FirstOrDefaultAsync(ct);

        if (account is null || account.IsDisabled)
            return Result.Failure<AdminActor>("The operator account is not available.", ErrorKind.Unauthorized);

        // The Control Plane ticket marks every admitted operator as an operator. The
        // persisted legacy IsAdmin bit is intentionally true only for SystemAdministrator;
        // requiring it here would make Support, Billing and Auditor unusable.
        if (!Enum.TryParse<OperatorRole>(account.Role, ignoreCase: false, out var role)
            || !Enum.IsDefined(role))
            return Result.Failure<AdminActor>("The account has no valid persisted operator role.", ErrorKind.Forbidden);
        if (account.IsAdmin != (role == OperatorRole.SystemAdministrator))
            return Result.Failure<AdminActor>("The operator role and legacy administrator marker are inconsistent.", ErrorKind.Forbidden);

        var permissions = OperatorRolePermissionSets.For(role);
        if (!OperatorPermissionSets.Includes(permissions, required))
            return Result.Failure<AdminActor>("The operator does not have the required permission.", ErrorKind.Forbidden);

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? elevatedUntil = account.TotpEnabled
            && actor.OperatorElevatedUntilUtc is { } claimExpiry
            && claimExpiry > now
                ? claimExpiry
                : null;
        if (requireElevation && elevatedUntil is null)
            return Result.Failure<AdminActor>(
                "Recent Control Plane authenticator verification is required for this operation.",
                ErrorKind.Forbidden);

        return Result.Ok(new AdminActor(userId, permissions, elevatedUntil));
    }

    private sealed record OperatorAccountState(bool IsAdmin, bool IsDisabled, bool TotpEnabled, string? Role);
}
