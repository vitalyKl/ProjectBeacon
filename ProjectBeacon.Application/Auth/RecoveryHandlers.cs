namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Application.Security;
using Domain.Entities.Identity;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
/// <summary>
/// Resets the admin password when the bootstrap token matches. This is break-glass, not the user reset flow.
/// </summary>
public class RecoverAdminHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IPasswordHasher _passwords;
    private readonly string _bootstrapToken;

    public RecoverAdminHandler(IBeaconDbFactory dbFactory, IPasswordHasher passwords, IConfiguration? configuration)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _bootstrapToken = configuration?["BOOTSTRAP_ADMIN_TOKEN"] ?? string.Empty;
    }

    public async Task<Result<RecoverAdminResponse>> HandleAsync(string providedToken, CancellationToken ct = default)
    {
        if (_bootstrapToken.Length == 0)
            return Result.Failure<RecoverAdminResponse>("Bootstrap token is not configured.", ErrorKind.Unauthorized);

        if (!FixedTimeEquals(providedToken, _bootstrapToken))
            return Result.Failure<RecoverAdminResponse>("Invalid bootstrap token.", ErrorKind.Unauthorized);

        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Login == "admin", ct)
            ?? await db.Users.FirstOrDefaultAsync(u => u.IsAdmin, ct);
        if (user is null)
            return Result.Failure<RecoverAdminResponse>("Admin account not found.");

        var password = _passwords.GenerateRandomPassword(32);
        user.ResetPassword(_passwords.Hash(password));
        await db.SaveChangesAsync(ct);

        return Result.Ok(new RecoverAdminResponse(user.Id, user.Login, user.Email, user.IsAdmin, password));
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
