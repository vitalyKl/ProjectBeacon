namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Application.Audit;
using Application.Security;
using Domain.Entities.Identity;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
/// <summary>
/// Creates the first admin when the bootstrap token matches and no admin exists yet. A second call conflicts.
/// </summary>
public class BootstrapHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IPasswordHasher _passwords;
    private readonly string _bootstrapToken;
    private readonly IAuditWriter? _auditWriter;

    public BootstrapHandler(IBeaconDbFactory dbFactory, IPasswordHasher passwords, IConfiguration? configuration, IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _bootstrapToken = configuration?["BOOTSTRAP_ADMIN_TOKEN"] ?? string.Empty;
        _auditWriter = auditWriter;
    }

    public async Task<Result<BootstrapResponse>> HandleAsync(string bootstrapToken, CancellationToken ct = default)
    {
        if (_bootstrapToken.Length == 0)
            return Result.Failure<BootstrapResponse>("Bootstrap token is not configured.", ErrorKind.Unauthorized);

        if (!FixedTimeEquals(bootstrapToken, _bootstrapToken))
            return Result.Failure<BootstrapResponse>("Invalid bootstrap token.", ErrorKind.Unauthorized);

        var globalTenant = new TenantContext();
        globalTenant.Assign(null, null, unscoped: true);
        await using var db = _dbFactory.CreateDbContext(globalTenant);
        var exists = await db.Users.AnyAsync(u => u.IsAdmin, ct);
        if (exists)
            return Result.Failure<BootstrapResponse>("Bootstrap already completed.", ErrorKind.Conflict);

        var password = _passwords.GenerateRandomPassword(32);
        var user = User.Create("admin", "admin@beacon.local",
            _passwords.Hash(password), isAdmin: true);

        db.Users.Add(user);
        _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
            "platform.bootstrap.completed", "User", user.Id.ToString(),
            detailsJson: """{"newState":"Active"}"""));
        await db.SaveChangesAsync(ct);

        return Result.Ok(new BootstrapResponse(
            user.Id, user.Login, user.Email, user.IsAdmin, password));
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
