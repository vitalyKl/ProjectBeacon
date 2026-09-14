namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Application.Identity;
using Domain.Entities.Identity;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;

public class BootstrapHandler
{
    private readonly BeaconDbContext _db;
    private readonly string _bootstrapToken;

    public BootstrapHandler(BeaconDbContext db, IConfiguration? configuration)
    {
        _db = db;
        _bootstrapToken = configuration?.GetValue<string>("BOOTSTRAP_ADMIN_TOKEN") ?? string.Empty;
    }

    public async Task<Result<BootstrapResponse>> HandleAsync(string bootstrapToken, CancellationToken ct = default)
    {
        var exists = await _db.Users.AnyAsync(u => u.IsAdmin, ct);
        if (exists)
            return Result.Failure<BootstrapResponse>("Bootstrap already completed.");

        if (_bootstrapToken.Length > 0 && !FixedTimeEquals(bootstrapToken, _bootstrapToken))
            return Result.Failure<BootstrapResponse>("Invalid bootstrap token.");

        var password = PasswordHasher.GenerateRandomPassword(32);
        var user = User.Create("admin", "admin@beacon.local",
            PasswordHasher.Hash(password), isAdmin: true);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

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

public class RecoverAdminHandler
{
    private readonly BeaconDbContext _db;
    private readonly string _bootstrapToken;

    public RecoverAdminHandler(BeaconDbContext db, IConfiguration? configuration)
    {
        _db = db;
        _bootstrapToken = configuration?.GetValue<string>("BOOTSTRAP_ADMIN_TOKEN") ?? string.Empty;
    }

    public async Task<Result<RecoverAdminResponse>> HandleAsync(string providedToken, CancellationToken ct = default)
    {
        if (_bootstrapToken.Length == 0)
            return Result.Failure<RecoverAdminResponse>("Bootstrap token is not configured.");

        if (!FixedTimeEquals(providedToken, _bootstrapToken))
            return Result.Failure<RecoverAdminResponse>("Invalid bootstrap token.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Login == "admin", ct)
            ?? await _db.Users.FirstOrDefaultAsync(u => u.IsAdmin, ct);
        if (user is null)
            return Result.Failure<RecoverAdminResponse>("Admin account not found.");

        var password = PasswordHasher.GenerateRandomPassword(32);
        user.ResetPassword(PasswordHasher.Hash(password));
        await _db.SaveChangesAsync(ct);

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

public class LoginHandler
{
    private readonly BeaconDbContext _db;
    private readonly IConfiguration? _configuration;

    public LoginHandler(BeaconDbContext db, IConfiguration? configuration = null)
    {
        _db = db;
        _configuration = configuration;
    }

    public async Task<Result<LoginResponse>> HandleAsync(LoginCommand command, CancellationToken ct = default)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Login == command.Request.Login || u.Email == command.Request.Login, ct);

        if (user is null)
            return Result.Failure<LoginResponse>("Invalid credentials.");

        if (user.IsLockedOut)
            return Result.Failure<LoginResponse>("Account is temporarily locked. Try again later.");

        if (!PasswordHasher.Verify(command.Request.Password, user.PasswordHash))
        {
            user.RecordFailedLogin();
            await _db.SaveChangesAsync(ct);
            return Result.Failure<LoginResponse>("Invalid credentials.");
        }

        user.RecordLogin();
        if (!string.IsNullOrEmpty(command.Request.IpAddress))
        {
            _db.Sessions.Add(UserSession.Create(user.Id, command.Request.IpAddress));
        }

        await _db.SaveChangesAsync(ct);

        var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(_db, user.Id, user.IsAdmin, ct);
        var secret = _configuration?["JWT:Secret"];
        var token = string.IsNullOrEmpty(secret)
            ? null
            : JwtTokenService.GenerateToken(user, secret, projectId: projectId, orgId: orgId);

        return Result.Ok(new LoginResponse(user.Id, user.Login, user.Email, user.IsAdmin, token));
    }
}

public class RegisterHandler
{
    private readonly BeaconDbContext _db;

    public RegisterHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<RegisterResponse>> HandleAsync(RegisterCommand command, CancellationToken ct = default)
    {
        var existing = await _db.Users.AnyAsync(u => u.Login == command.Request.Login, ct);
        if (existing)
            return Result.Failure<RegisterResponse>("Login already taken.");

        var existingEmail = await _db.Users.AnyAsync(u => u.Email == command.Request.Email, ct);
        if (existingEmail)
            return Result.Failure<RegisterResponse>("Email already registered.");

        var user = User.Create(command.Request.Login, command.Request.Email,
            PasswordHasher.Hash(command.Request.Password));

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(new RegisterResponse(user.Id, user.Login, user.Email));
    }
}
