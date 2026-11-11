namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Application.Audit;
using Application.Security;
using Domain.Entities.Identity;
using Domain.Entities.Audit;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Creates the first admin when the bootstrap token matches and no admin exists yet. A second call conflicts.
/// </summary>
public class BootstrapHandler
{
    private static readonly SemaphoreSlim BootstrapGate = new(1, 1);

    private readonly IBeaconDbFactory _dbFactory;
    private readonly IPasswordHasher _passwords;
    private readonly string _bootstrapToken;
    private readonly IAuditWriter? _auditWriter;
    private readonly ILogger<BootstrapHandler>? _logger;

    public BootstrapHandler(
        IBeaconDbFactory dbFactory,
        IPasswordHasher passwords,
        IConfiguration? configuration,
        IAuditWriter? auditWriter = null,
        ILogger<BootstrapHandler>? logger = null)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _bootstrapToken = configuration?["BOOTSTRAP_ADMIN_TOKEN"] ?? string.Empty;
        _auditWriter = auditWriter;
        _logger = logger;
    }

    public async Task<Result<BootstrapResponse>> HandleAsync(string bootstrapToken, CancellationToken ct = default)
    {
        if (_bootstrapToken.Length == 0)
        {
            await RecordDeniedAttemptAsync("bootstrap_token_unconfigured", ct);
            return Result.Failure<BootstrapResponse>("Bootstrap token is not configured.", ErrorKind.Unauthorized);
        }

        if (!FixedTimeEquals(bootstrapToken, _bootstrapToken))
        {
            await RecordDeniedAttemptAsync("bootstrap_token_invalid", ct);
            return Result.Failure<BootstrapResponse>("Invalid bootstrap token.", ErrorKind.Unauthorized);
        }

        // Bootstrap is a one-time global transition. Serialize callers within this process
        // and across Web replicas, then keep the existence check, user insert, and audit event
        // in one transaction. A transaction-scoped advisory lock is released automatically on
        // commit/rollback and is shared by every PostgreSQL connection to this database.
        await BootstrapGate.WaitAsync(ct);
        try
        {
            var globalTenant = new TenantContext();
            globalTenant.Assign(null, null, unscoped: true);
            await using var db = _dbFactory.CreateDbContext(globalTenant);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            if (string.Equals(
                db.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(20261111, 97)",
                    ct);
            }

            var exists = await db.Users.AnyAsync(u => u.IsAdmin, ct);
            if (exists)
            {
                _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
                    "platform.bootstrap.denied",
                    "AdminBootstrap",
                    null,
                    outcome: AuditOutcome.Denied,
                    detailsJson: """{"reasonCode":"bootstrap_already_completed"}"""));
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Result.Failure<BootstrapResponse>("Bootstrap already completed.", ErrorKind.Conflict);
            }

            var password = _passwords.GenerateRandomPassword(32);
            var user = User.Create("admin", "admin@beacon.local",
                _passwords.Hash(password), isAdmin: true);

            db.Users.Add(user);
            _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
                "platform.bootstrap.completed", "User", user.Id.ToString(),
                detailsJson: """{"newState":"Active"}"""));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Result.Ok(new BootstrapResponse(
                user.Id, user.Login, user.Email, user.IsAdmin, password));
        }
        finally
        {
            BootstrapGate.Release();
        }
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
                "platform.bootstrap.denied",
                "AdminBootstrap",
                null,
                outcome: AuditOutcome.Denied,
                detailsJson: $$"""{"reasonCode":"{{reasonCode}}"}"""));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed denial-log write must not replace the original authentication result.
            _logger?.LogWarning(ex, "Could not persist an audit record for a denied admin bootstrap attempt.");
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
