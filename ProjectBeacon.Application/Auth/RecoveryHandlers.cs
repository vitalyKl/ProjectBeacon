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
    private readonly IUserSessionVersionCache? _sessionVersions;

    public RecoverAdminHandler(
        IBeaconDbFactory dbFactory,
        IPasswordHasher passwords,
        IConfiguration? configuration,
        IAuditWriter? auditWriter = null,
        ILogger<RecoverAdminHandler>? logger = null,
        IUserSessionVersionCache? sessionVersions = null)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _bootstrapToken = configuration?["BOOTSTRAP_ADMIN_TOKEN"] ?? string.Empty;
        _auditWriter = auditWriter;
        _logger = logger;
        _sessionVersions = sessionVersions;
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

        // Recovery is restricted to an active platform SystemAdministrator. A legacy
        // login named "admin" is not sufficient to claim the break-glass recovery path.
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Login == "admin" && u.IsAdmin && u.DisabledAt == null, ct)
            ?? await db.Users
                .Where(u => u.IsAdmin && u.DisabledAt == null)
                .OrderBy(u => u.Login)
                .FirstOrDefaultAsync(ct);
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
        var authenticatorReset = user.TotpEnabled || !string.IsNullOrWhiteSpace(user.TotpSecretCipher);

        // ResetPassword increments SessionVersion; clearing both an enabled and a pending
        // TOTP enrollment in the same unit of work completes break-glass MFA recovery.
        user.ResetPassword(_passwords.Hash(password));
        user.ClearTotp();

        _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
            "platform.admin_recovery.completed",
            "User",
            user.Id.ToString(),
            outcome: AuditOutcome.Success,
            detailsJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                resultCode = "credentials_reset",
                authenticatorReset
            })));
        if (authenticatorReset)
        {
            _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
                "platform.admin_recovery.authenticator_reset",
                "User",
                user.Id.ToString(),
                outcome: AuditOutcome.Success,
                detailsJson: """{"reasonCode":"break_glass","newState":"Disabled"}"""));
        }
        await db.SaveChangesAsync(ct);
        _sessionVersions?.Invalidate(user.Id);

        return Result.Ok(new RecoverAdminResponse(
            user.Id, user.Login, user.Email, user.IsAdmin, password, authenticatorReset));
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
