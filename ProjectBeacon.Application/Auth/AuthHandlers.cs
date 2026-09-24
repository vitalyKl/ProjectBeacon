namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Application.Identity;
using Domain.Entities.Identity;
using Infrastructure.Data;
using Infrastructure.Mail;
using Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;

public class BootstrapHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly string _bootstrapToken;

    public BootstrapHandler(IDbContextFactory<BeaconDbContext> dbFactory, IConfiguration? configuration)
    {
        _dbFactory = dbFactory;
        _bootstrapToken = configuration?.GetValue<string>("BOOTSTRAP_ADMIN_TOKEN") ?? string.Empty;
    }

    public async Task<Result<BootstrapResponse>> HandleAsync(string bootstrapToken, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var exists = await db.Users.AnyAsync(u => u.IsAdmin, ct);
        if (exists)
            return Result.Failure<BootstrapResponse>("Bootstrap already completed.");

        if (_bootstrapToken.Length > 0 && !FixedTimeEquals(bootstrapToken, _bootstrapToken))
            return Result.Failure<BootstrapResponse>("Invalid bootstrap token.");

        var password = PasswordHasher.GenerateRandomPassword(32);
        var user = User.Create("admin", "admin@beacon.local",
            PasswordHasher.Hash(password), isAdmin: true);

        db.Users.Add(user);
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

public class RecoverAdminHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly string _bootstrapToken;

    public RecoverAdminHandler(IDbContextFactory<BeaconDbContext> dbFactory, IConfiguration? configuration)
    {
        _dbFactory = dbFactory;
        _bootstrapToken = configuration?.GetValue<string>("BOOTSTRAP_ADMIN_TOKEN") ?? string.Empty;
    }

    public async Task<Result<RecoverAdminResponse>> HandleAsync(string providedToken, CancellationToken ct = default)
    {
        if (_bootstrapToken.Length == 0)
            return Result.Failure<RecoverAdminResponse>("Bootstrap token is not configured.");

        if (!FixedTimeEquals(providedToken, _bootstrapToken))
            return Result.Failure<RecoverAdminResponse>("Invalid bootstrap token.");

        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Login == "admin", ct)
            ?? await db.Users.FirstOrDefaultAsync(u => u.IsAdmin, ct);
        if (user is null)
            return Result.Failure<RecoverAdminResponse>("Admin account not found.");

        var password = PasswordHasher.GenerateRandomPassword(32);
        user.ResetPassword(PasswordHasher.Hash(password));
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

public class LoginHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly IConfiguration? _configuration;

    public LoginHandler(IDbContextFactory<BeaconDbContext> dbFactory, IConfiguration? configuration = null)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public async Task<Result<LoginResponse>> HandleAsync(LoginCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Login == command.Request.Login || u.Email == command.Request.Login, ct);

        if (user is null)
            return Result.Failure<LoginResponse>("Invalid credentials.");

        if (user.IsLockedOut)
            return Result.Failure<LoginResponse>("Account is temporarily locked. Try again later.");

        if (!PasswordHasher.Verify(command.Request.Password, user.PasswordHash))
        {
            user.RecordFailedLogin();
            await db.SaveChangesAsync(ct);
            return Result.Failure<LoginResponse>("Invalid credentials.");
        }

        user.RecordLogin();
        if (!string.IsNullOrEmpty(command.Request.IpAddress))
        {
            db.Sessions.Add(UserSession.Create(user.Id, command.Request.IpAddress));
        }

        await db.SaveChangesAsync(ct);

        var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(db, user.Id, user.IsAdmin, null, ct);
        var secret = _configuration?["JWT:Secret"];
        var token = string.IsNullOrEmpty(secret)
            ? null
            : JwtTokenService.GenerateToken(user, secret, projectId: projectId, orgId: orgId);

        return Result.Ok(new LoginResponse(user.Id, user.Login, user.Email, user.IsAdmin, token));
    }
}

public class RegisterHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly IConfiguration? _configuration;

    public RegisterHandler(IDbContextFactory<BeaconDbContext> dbFactory, IConfiguration? configuration = null)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public async Task<Result<RegisterResponse>> HandleAsync(RegisterCommand command, CancellationToken ct = default)
    {
        var login = command.Request.Login?.Trim() ?? string.Empty;
        var email = command.Request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var password = command.Request.Password ?? string.Empty;
        var inviteToken = command.Request.InviteToken?.Trim();

        if (login.Length == 0 || email.Length == 0)
            return Result.Failure<RegisterResponse>("Login and email are required.");
        if (password.Length < 8)
            return Result.Failure<RegisterResponse>("Password must be at least 8 characters.");

        var inviteOnly = LocalAuthOptions.InviteOnly(_configuration);
        if (inviteOnly && string.IsNullOrEmpty(inviteToken))
            return Result.Failure<RegisterResponse>("Invite required.");

        await using var db = _dbFactory.CreateDbContext();
        var existing = await db.Users.AnyAsync(u => u.Login == login, ct);
        if (existing)
            return Result.Failure<RegisterResponse>("Login already taken.");

        var existingEmail = await db.Users.AnyAsync(u => u.Email == email, ct);
        if (existingEmail)
            return Result.Failure<RegisterResponse>("Email already registered.");

        Domain.Entities.Identity.OrgInvite? orgInvite = null;
        Domain.Entities.Projects.ProjectInvite? projectInvite = null;
        if (!string.IsNullOrEmpty(inviteToken))
        {
            var hash = OpaqueToken.Hash(inviteToken);
            orgInvite = await db.OrgInvites.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
            projectInvite = orgInvite is null
                ? await db.ProjectInvites.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
                : null;
            if (orgInvite is null && projectInvite is null)
                return Result.Failure<RegisterResponse>("Invite not found.");
            var inviteEmail = orgInvite?.Email ?? projectInvite!.Email;
            if (!string.Equals(inviteEmail, email, StringComparison.Ordinal))
                return Result.Failure<RegisterResponse>("Email does not match invite.");
            if (orgInvite is not null && (orgInvite.Status != Domain.Enums.InviteStatus.Pending || orgInvite.ExpiredAt < DateTime.UtcNow))
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");
            if (projectInvite is not null && (projectInvite.Status != Domain.Enums.InviteStatus.Pending || projectInvite.ExpiredAt < DateTime.UtcNow))
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");
        }

        var user = User.Create(login, email, PasswordHasher.Hash(password));
        db.Users.Add(user);

        if (orgInvite is not null)
        {
            if (!orgInvite.TryAccept())
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");
            if (!await db.OrgMembers.IgnoreQueryFilters()
                    .AnyAsync(m => m.OrgId == orgInvite.OrgId && m.UserId == user.Id, ct))
                db.OrgMembers.Add(OrgMember.Create(orgInvite.OrgId, user.Id, orgInvite.Role));
        }

        if (projectInvite is not null)
        {
            if (!projectInvite.TryAccept())
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");
            var project = await db.Projects.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == projectInvite.ProjectId, ct);
            if (project is null)
                return Result.Failure<RegisterResponse>("Project not found.");
            if (!await db.OrgMembers.IgnoreQueryFilters()
                    .AnyAsync(m => m.OrgId == project.OrgId && m.UserId == user.Id, ct))
                db.OrgMembers.Add(OrgMember.Create(project.OrgId, user.Id, Domain.Enums.MemberRole.Member));
            if (!await db.ProjectMembers.IgnoreQueryFilters()
                    .AnyAsync(m => m.ProjectId == projectInvite.ProjectId && m.UserId == user.Id, ct))
                db.ProjectMembers.Add(Domain.Entities.Projects.ProjectMember.Create(
                    projectInvite.ProjectId, user.Id, projectInvite.Role));
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(new RegisterResponse(user.Id, user.Login, user.Email));
    }
}

public class ForgotPasswordHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly IEmailSender _email;
    private readonly IConfiguration? _configuration;

    public ForgotPasswordHandler(
        IDbContextFactory<BeaconDbContext> dbFactory,
        IEmailSender email,
        IConfiguration? configuration = null)
    {
        _dbFactory = dbFactory;
        _email = email;
        _configuration = configuration;
    }

    public async Task<Result> HandleAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        await using var db = _dbFactory.CreateDbContext();
        var user = string.IsNullOrEmpty(email)
            ? null
            : await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
            return Result.Ok();

        var raw = OpaqueToken.Generate(OpaqueToken.ResetPrefix);
        db.PasswordResetTokens.Add(PasswordResetToken.Create(user.Id, OpaqueToken.Hash(raw)));
        await db.SaveChangesAsync(ct);

        var baseUrl = LocalAuthOptions.PublicUrl(_configuration);
        var link = string.IsNullOrEmpty(baseUrl) ? $"/reset?token={raw}" : $"{baseUrl}/reset?token={raw}";
        try
        {
            await _email.SendAsync(user.Email, "Reset your ProjectBeacon password",
                $"Use this link to set a new password (expires in 1 hour):\n{link}\n", ct);
        }
        catch
        {
        }

        return Result.Ok();
    }
}

public class ResetPasswordHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ResetPasswordHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var password = request.Password ?? string.Empty;
        if (password.Length < 8)
            return Result.Failure("Password must be at least 8 characters.");
        var token = request.Token?.Trim() ?? string.Empty;
        if (token.Length == 0)
            return Result.Failure("Invalid or expired token.");

        await using var db = _dbFactory.CreateDbContext();
        var hash = OpaqueToken.Hash(token);
        var reset = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (reset is null || !reset.TryConsume())
            return Result.Failure("Invalid or expired token.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == reset.UserId, ct);
        if (user is null)
            return Result.Failure("Invalid or expired token.");

        user.ResetPassword(PasswordHasher.Hash(password));
        var sessions = await db.Sessions.Where(s => s.UserId == user.Id && s.IsActive).ToListAsync(ct);
        foreach (var session in sessions)
            session.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public class ChangePasswordHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ChangePasswordHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(ChangePasswordRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 8)
            return Result.Failure("Password must be at least 8 characters.");

        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null || !PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            return Result.Failure("Current password is incorrect.");

        user.ResetPassword(PasswordHasher.Hash(request.NewPassword));
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}