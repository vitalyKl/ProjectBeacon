namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Application.Security;
using Application.Audit;
using Domain.Entities.Identity;
using Domain.Entities.Audit;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

/// <summary>
/// Resets the admin password when the bootstrap token matches. This is break-glass, not the user reset flow.
/// </summary>
public class RecoverAdminHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IPasswordHasher _passwords;
    private readonly string _bootstrapToken;
    private readonly IAuditWriter? _auditWriter;
    private readonly ILogger<RecoverAdminHandler>? _logger;

    public RecoverAdminHandler(
        IBeaconDbFactory dbFactory,
        IPasswordHasher passwords,
        IConfiguration? configuration,
        IAuditWriter? auditWriter = null,
        ILogger<RecoverAdminHandler>? logger = null)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _bootstrapToken = configuration?["BOOTSTRAP_ADMIN_TOKEN"] ?? string.Empty;
        _auditWriter = auditWriter;
        _logger = logger;
    }

    public async Task<Result<RecoverAdminResponse>> HandleAsync(string providedToken, CancellationToken ct = default)
    {
        if (_bootstrapToken.Length == 0)
        {
            await RecordDeniedAttemptAsync("bootstrap_token_unconfigured", ct);
            return Result.Failure<RecoverAdminResponse>("Bootstrap token is not configured.", ErrorKind.Unauthorized);
        }

        if (!FixedTimeEquals(providedToken, _bootstrapToken))
        {
            await RecordDeniedAttemptAsync("bootstrap_token_invalid", ct);
            return Result.Failure<RecoverAdminResponse>("Invalid bootstrap token.", ErrorKind.Unauthorized);
        }

        // Recovery is a platform-wide operation. Do not inherit an ambient organization/project scope.
        var globalTenant = new TenantContext();
        globalTenant.Assign(null, null, unscoped: true);
        await using var db = _dbFactory.CreateDbContext(globalTenant);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Login == "admin", ct)
            ?? await db.Users.FirstOrDefaultAsync(u => u.IsAdmin, ct);
        if (user is null)
        {
            _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
                "platform.admin_recovery.failed",
                "AdminRecovery",
                null,
                outcome: AuditOutcome.Failed,
                detailsJson: """{"reasonCode":"admin_account_missing"}"""));
            await db.SaveChangesAsync(ct);
            return Result.Failure<RecoverAdminResponse>("Admin account not found.");
        }

        var password = _passwords.GenerateRandomPassword(32);
        user.ResetPassword(_passwords.Hash(password));
        _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
            "platform.admin_recovery.completed",
            "User",
            user.Id.ToString(),
            outcome: AuditOutcome.Success,
            detailsJson: """{"resultCode":"password_reset"}"""));
        await db.SaveChangesAsync(ct);

        return Result.Ok(new RecoverAdminResponse(user.Id, user.Login, user.Email, user.IsAdmin, password));
    }

    private async Task RecordDeniedAttemptAsync(string reasonCode, CancellationToken ct)
    {
        if (_auditWriter is null)
            return;

        try
        {
            var globalTenant = new TenantContext();
            globalTenant.Assign(null, null, unscoped: true);
            await using var db = _dbFactory.CreateDbContext(globalTenant);
            _auditWriter.Stage(db, AuditEventFactory.ForSystem(
                "platform.admin_recovery.denied",
                "AdminRecovery",
                null,
                outcome: AuditOutcome.Denied,
                detailsJson: $$"""{"reasonCode":"{{reasonCode}}"}"""));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed denial-log write must not replace the original authentication result.
            _logger?.LogWarning(ex, "Could not persist an audit record for a denied admin recovery attempt.");
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
