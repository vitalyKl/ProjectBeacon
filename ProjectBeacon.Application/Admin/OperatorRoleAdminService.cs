namespace ProjectBeacon.Application.Admin;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Enums;

public sealed record OperatorAccountDto(
    Guid UserId, string Login, string Email, string? OperatorRole,
    bool IsDisabled, bool TotpEnabled, int SessionVersion);

public sealed class OperatorRoleAdminService(
    IBeaconDbFactory dbFactory,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<Result<IReadOnlyList<OperatorAccountDto>>> ListAsync(
        AdminActor actor, CancellationToken ct = default)
    {
        if (!actor.HasPermission(OperatorPermission.OperatorRolesManage))
            return Result.Failure<IReadOnlyList<OperatorAccountDto>>(
                "OperatorRolesManage permission is required.", ErrorKind.Forbidden);

        await using var db = dbFactory.CreateDbContext(GlobalTenant());
        var rows = await db.Users.AsNoTracking()
            .OrderBy(user => user.Login)
            .Take(500)
            .Select(user => new OperatorAccountDto(
                user.Id, user.Login, user.Email, user.OperatorRole,
                user.DisabledAt != null, user.TotpEnabled, user.SessionVersion))
            .ToListAsync(ct);
        return Result.Ok<IReadOnlyList<OperatorAccountDto>>(rows);
    }

    public async Task<Result> AssignAsync(
        AdminActor actor, Guid targetUserId, OperatorRole role, CancellationToken ct = default)
    {
        var authorization = ValidateMutation(actor, targetUserId, role);
        if (authorization is not null)
            return authorization;

        await using var db = dbFactory.CreateDbContext(GlobalTenant());
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AcquireMutationLockAsync(db, transaction, ct);

        var user = await db.Users.FirstOrDefaultAsync(item => item.Id == targetUserId, ct);
        if (user is null)
            return Result.Failure("Account not found.", ErrorKind.NotFound);
        if (user.IsDisabled)
            return Result.Failure("A disabled account cannot be assigned an operator role.", ErrorKind.Conflict);

        var previousRole = ParseRole(user.OperatorRole);
        if (previousRole == role)
            return Result.Ok();

        if (previousRole == OperatorRole.SystemAdministrator
            && role != OperatorRole.SystemAdministrator
            && await CountActiveSystemAdministratorsAsync(db, ct) <= 1)
            return Result.Failure("The last active SystemAdministrator cannot be demoted.", ErrorKind.Conflict);

        user.AssignOperatorRole(role.ToString());
        StageAudit(db, actor, user.Id, "platform.operator_role.assigned",
            previousRole?.ToString() ?? "None", role.ToString());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Ok();
    }

    public async Task<Result> RemoveAsync(
        AdminActor actor, Guid targetUserId, CancellationToken ct = default)
    {
        var authorization = ValidateMutation(actor, targetUserId, null);
        if (authorization is not null)
            return authorization;

        await using var db = dbFactory.CreateDbContext(GlobalTenant());
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AcquireMutationLockAsync(db, transaction, ct);

        var user = await db.Users.FirstOrDefaultAsync(item => item.Id == targetUserId, ct);
        if (user is null)
            return Result.Failure("Account not found.", ErrorKind.NotFound);

        var previousRole = ParseRole(user.OperatorRole);
        if (previousRole is null)
            return Result.Failure("Account does not have a valid operator role.", ErrorKind.Conflict);
        if (previousRole == OperatorRole.SystemAdministrator
            && await CountActiveSystemAdministratorsAsync(db, ct) <= 1)
            return Result.Failure("The last active SystemAdministrator cannot be removed.", ErrorKind.Conflict);

        user.RemoveOperatorRole();
        StageAudit(db, actor, user.Id, "platform.operator_role.removed",
            previousRole.Value.ToString(), "None");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Ok();
    }

    private Result? ValidateMutation(AdminActor actor, Guid targetUserId, OperatorRole? role)
    {
        if (!actor.HasPermission(OperatorPermission.OperatorRolesManage))
            return Result.Forbidden("OperatorRolesManage permission is required.");
        if (!actor.IsElevatedAt(timeProvider.GetUtcNow()))
            return Result.Forbidden("Recent Control Plane authenticator verification is required.");
        if (targetUserId == Guid.Empty)
            return Result.Failure("Account id is required.");
        if (actor.UserId == targetUserId)
            return Result.Forbidden("An operator cannot change their own role.");
        if (role is { } value && !Enum.IsDefined(value))
            return Result.Failure("Operator role is invalid.", ErrorKind.Validation);
        return null;
    }

    private static OperatorRole? ParseRole(string? value) =>
        Enum.TryParse<OperatorRole>(value, ignoreCase: false, out var role) && Enum.IsDefined(role)
            ? role : null;

    private static Task<int> CountActiveSystemAdministratorsAsync(IBeaconDb db, CancellationToken ct) =>
        db.Users.CountAsync(user => user.IsAdmin
            && user.OperatorRole == nameof(OperatorRole.SystemAdministrator)
            && user.DisabledAt == null, ct);

    private void StageAudit(
        IBeaconDb db, AdminActor actor, Guid targetUserId, string action, string previousRole, string newRole)
    {
        var details = System.Text.Json.JsonSerializer.Serialize(new { previousState = previousRole, newState = newRole });
        auditWriter.Stage(db, AuditEvent.Create(
            timeProvider.GetUtcNow().UtcDateTime, AuditActorKind.Human, actor.UserId, null, null,
            action, "User", targetUserId.ToString(), null, null, AuditOutcome.Success,
            detailsJson: details));
    }

    private static async Task AcquireMutationLockAsync(
        IBeaconDb db, IDbContextTransaction transaction, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return;
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(728451203118)";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static TenantContext GlobalTenant()
    {
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        return tenant;
    }
}
