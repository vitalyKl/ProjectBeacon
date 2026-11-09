namespace ProjectBeacon.Application.Auth;

using Application.Audit;
using Application.Common;
using Application.Identity;
using Application.Security;
using Domain.Entities.Identity;
using Domain.Entities.Audit;
using Application.Persistence;
using Application.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
/// <summary>
/// Always succeeds, whether or not the email is registered.
/// </summary>
public class ForgotPasswordHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IEmailSender _email;
    private readonly IConfiguration? _configuration;
    private readonly IAuditWriter? _auditWriter;

    public ForgotPasswordHandler(
        IBeaconDbFactory dbFactory,
        IEmailSender email,
        IConfiguration? configuration = null,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _email = email;
        _configuration = configuration;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var unscopedTenant = new TenantContext();
        unscopedTenant.Assign(null, null, unscoped: true);
        await using var db = _dbFactory.CreateDbContext(unscopedTenant);
        var user = string.IsNullOrEmpty(email)
            ? null
            : await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
            return Result.Ok();

        var raw = OpaqueToken.Generate(OpaqueToken.ResetPrefix);
        db.PasswordResetTokens.Add(PasswordResetToken.Create(user.Id, OpaqueToken.Hash(raw)));
        _auditWriter?.Stage(db, AuditEvent.Create(
            DateTime.UtcNow,
            AuditActorKind.System,
            null,
            null,
            null,
            "account.password_reset_requested",
            "User",
            user.Id.ToString(),
            null,
            null,
            AuditOutcome.Success,
            detailsJson: """{"authMethod":"email"}"""));
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
/// <summary>
/// Consumes a reset token and sets a password of at least 8 characters. A missing or used token fails the same way.
/// </summary>
public class ResetPasswordHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IPasswordHasher _passwords;
    private readonly IUserSessionVersionCache? _sessionVersions;
    private readonly IAuditWriter? _auditWriter;

    public ResetPasswordHandler(
        IBeaconDbFactory dbFactory,
        IPasswordHasher passwords,
        IUserSessionVersionCache? sessionVersions = null,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _sessionVersions = sessionVersions;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var password = request.Password ?? string.Empty;
        if (password.Length < 8)
            return Result.Failure("Password must be at least 8 characters.");
        var token = request.Token?.Trim() ?? string.Empty;
        if (token.Length == 0)
            return Result.Failure("Invalid or expired token.");

        var unscopedTenant = new TenantContext();
        unscopedTenant.Assign(null, null, unscoped: true);
        await using var db = _dbFactory.CreateDbContext(unscopedTenant);
        var hash = OpaqueToken.Hash(token);
        var reset = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (reset is null || !reset.TryConsume())
            return Result.Failure("Invalid or expired token.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == reset.UserId, ct);
        if (user is null)
            return Result.Failure("Invalid or expired token.");

        user.ResetPassword(_passwords.Hash(password));
        var sessions = await db.Sessions.Where(s => s.UserId == user.Id && s.IsActive).ToListAsync(ct);
        foreach (var session in sessions)
            session.Deactivate();
        _auditWriter?.Stage(db, AuditEvent.Create(
            DateTime.UtcNow,
            AuditActorKind.Human,
            user.Id,
            null,
            null,
            "account.password_reset",
            "User",
            user.Id.ToString(),
            null,
            null,
            AuditOutcome.Success,
            detailsJson: """{"authMethod":"email-reset-token"}"""));
        await db.SaveChangesAsync(ct);
        _sessionVersions?.Invalidate(user.Id);
        return Result.Ok();
    }
}
/// <summary>
/// Replaces the password after the current one matches. The new password must be at least 8 characters.
/// </summary>
public class ChangePasswordHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IPasswordHasher _passwords;
    private readonly IUserSessionVersionCache? _sessionVersions;
    private readonly IAuditWriter? _auditWriter;

    public ChangePasswordHandler(
        IBeaconDbFactory dbFactory,
        IPasswordHasher passwords,
        IUserSessionVersionCache? sessionVersions = null,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _sessionVersions = sessionVersions;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(ChangePasswordRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 8)
            return Result.Failure("Password must be at least 8 characters.");

        var unscopedTenant = new TenantContext();
        unscopedTenant.Assign(null, null, unscoped: true);
        await using var db = _dbFactory.CreateDbContext(unscopedTenant);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null || !_passwords.Verify(request.CurrentPassword, user.PasswordHash))
            return Result.Failure("Current password is incorrect.");

        user.ResetPassword(_passwords.Hash(request.NewPassword));
        _auditWriter?.Stage(db, AuditEvent.Create(
            DateTime.UtcNow,
            AuditActorKind.Human,
            user.Id,
            null,
            null,
            "account.password_changed",
            "User",
            user.Id.ToString(),
            null,
            null,
            AuditOutcome.Success,
            detailsJson: """{"authMethod":"current-password"}"""));
        await db.SaveChangesAsync(ct);
        _sessionVersions?.Invalidate(user.Id);
        return Result.Ok();
    }
}
