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

        // Bootstrap is a one-time global transition. Serialize callers within this process,
        // and use a transaction-scoped advisory lock to serialize independent Web replicas.
        // Npgsql production options enable retries, so the whole transaction must run inside
        // the provider execution strategy. The generated user ID and password are stable
        // across retries: if a commit succeeds but its acknowledgement is lost, the next
        // attempt recognizes its own user row and returns the original one-time credentials.
        await BootstrapGate.WaitAsync(ct);
        try
        {
            var globalTenant = new TenantContext();
            globalTenant.Assign(null, null, unscoped: true);
            await using var db = _dbFactory.CreateDbContext(globalTenant);
            var strategy = db.Database.CreateExecutionStrategy();

            var password = _passwords.GenerateRandomPassword(32);
            var user = User.Create(
                "admin",
                "admin@beacon.local",
                _passwords.Hash(password),
                isAdmin: true);

            var completedEvent = _auditWriter is null
                ? null
                : AuditEventFactory.ForSystem(
                    "platform.bootstrap.completed",
                    "User",
                    user.Id.ToString(),
                    detailsJson: """{"newState":"Active"}""");
            var deniedEvent = _auditWriter is null
                ? null
                : AuditEventFactory.ForSystem(
                    "platform.bootstrap.denied",
                    "AdminBootstrap",
                    null,
                    outcome: AuditOutcome.Denied,
                    detailsJson: """{"reasonCode":"bootstrap_already_completed"}""");

            return await strategy.ExecuteAsync(async () =>
            {
                // A transient database failure may re-enter this delegate. Detach entities
                // from the failed attempt before attaching the stable user/audit instances.
                db.ChangeTracker.Clear();
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

                var existingAdminId = await db.Users
                    .Where(item => item.IsAdmin)
                    .Select(item => (Guid?)item.Id)
                    .FirstOrDefaultAsync(ct);

                if (existingAdminId is { } existingId)
                {
                    await transaction.CommitAsync(ct);

                    // The preceding attempt may have committed successfully but lost its
                    // acknowledgement. In that case this is our own bootstrap, not a
                    // competing administrator; return the same one-time password.
                    if (existingId == user.Id)
                    {
                        return Result.Ok(new BootstrapResponse(
                            user.Id, user.Login, user.Email, user.IsAdmin, password));
                    }

                    // The denial event has a stable ID so an execution-strategy retry does
                    // not write duplicate audit rows if the prior commit acknowledgement was
                    // lost after the database committed it.
                    if (deniedEvent is not null
                        && !await db.AuditEvents.AnyAsync(item => item.Id == deniedEvent.Id, ct))
                    {
                        _auditWriter!.Stage(db, deniedEvent);
                        await db.SaveChangesAsync(ct);
                        await transaction.CommitAsync(ct);
                    }

                    return Result.Failure<BootstrapResponse>(
                        "Bootstrap already completed.", ErrorKind.Conflict);
                }

                db.Users.Add(user);
                if (completedEvent is not null)
                    _auditWriter!.Stage(db, completedEvent);

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return Result.Ok(new BootstrapResponse(
                    user.Id, user.Login, user.Email, user.IsAdmin, password));
            }, ct);
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
