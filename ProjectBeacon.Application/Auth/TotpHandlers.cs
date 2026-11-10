namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Application.Audit;
using ProjectBeacon.Application.Admin;
using Application.Authorization;
using Domain.Enums;
using Application.Security;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Security.Cryptography;
using System.Text;
/// <summary>
/// Whether TOTP is enabled. OtpAuthUri and Secret are set only while setup is still pending.
/// </summary>
public record TotpStatusDto(bool Enabled, bool Pending, string? OtpAuthUri, string? Secret, int RecoveryCodeCount = 0);
public sealed record TotpConfirmationDto(IReadOnlyList<string> RecoveryCodes);
public sealed record TotpRecoveryCodesDto(IReadOnlyList<string> RecoveryCodes);
public sealed record RotateOperatorTotpRecoveryCodesCommand(AdminActor Actor, string Code);
public sealed record RecoverOperatorTotpCommand(AdminActor Actor, string RecoveryCode);
/// <summary>
/// Command for get totp status.
/// </summary>
public record GetTotpStatusCommand(Guid UserId) : ICommand<Result<TotpStatusDto>>;
/// <summary>
/// User starting authenticator setup.
/// </summary>
public record BeginTotpCommand(Guid UserId) : ICommand<Result<string>>;
/// <summary>
/// User and the authenticator code that enables TOTP.
/// </summary>
public record ConfirmTotpRequest(Guid UserId, string Code);
/// <summary>
/// Command for confirm totp.
/// </summary>
public record ConfirmTotpCommand(ConfirmTotpRequest Request) : ICommand<Result<TotpConfirmationDto>>;
/// <summary>
/// User and the authenticator code that turns TOTP off.
/// </summary>
public record DisableTotpRequest(Guid UserId, string Code);
/// <summary>
/// Command for disable totp.
/// </summary>
public record DisableTotpCommand(DisableTotpRequest Request) : ICommand<Result>;
/// <summary>
/// Reports whether TOTP is enabled. While setup is pending, also returns the otpauth URI and the opened secret.
/// </summary>
public sealed class GetTotpStatusHandler : ICommandHandler<GetTotpStatusCommand, Result<TotpStatusDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector _secrets;
    private readonly ITotp _totp;

    public GetTotpStatusHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets, ITotp totp)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _totp = totp;
    }

    public async Task<Result<TotpStatusDto>> HandleAsync(GetTotpStatusCommand command, CancellationToken ct = default)
    {
        var globalTenant = new TenantContext();
        globalTenant.Assign(null, null, unscoped: true);
        await using var db = _dbFactory.CreateDbContext(globalTenant);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, ct);
        if (user is null)
            return Result.Failure<TotpStatusDto>("Account is not resolved.");
        var pending = !user.TotpEnabled && !string.IsNullOrEmpty(user.TotpSecretCipher);
        string? uri = null;
        string? secret = null;
        if (pending)
        {
            try
            {
                secret = _secrets.Open(user.TotpSecretCipher);
                uri = _totp.OtpAuthUri(secret, user.Email);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                pending = false;
            }
        }
        return Result.Ok(new TotpStatusDto(user.TotpEnabled, pending, uri, secret, user.TotpRecoveryCodeCount));
    }
}
/// <summary>
/// Starts TOTP setup and returns the otpauth URI and the secret. The secret is not enabled until confirm.
/// </summary>
public sealed class BeginTotpHandler : ICommandHandler<BeginTotpCommand, Result<string>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector _secrets;
    private readonly ITotp _totp;
    private readonly IAuditWriter? _auditWriter;

    public BeginTotpHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets, ITotp totp, IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _totp = totp;
        _auditWriter = auditWriter;
    }

    public async Task<Result<string>> HandleAsync(BeginTotpCommand command, CancellationToken ct = default)
    {
        var globalTenant = new TenantContext();
        globalTenant.Assign(null, null, unscoped: true);
        await using var db = _dbFactory.CreateDbContext(globalTenant);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, ct);
        if (user is null)
            return Result.Failure<string>("Account is not resolved.");
        var secret = _totp.GenerateSecret();
        user.BeginTotp(_secrets.Seal(secret));
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None), "account.mfa.setup_started", "User", user.Id.ToString(), null, null,
            detailsJson: """{"newState":"Pending"}"""));
        await db.SaveChangesAsync(ct);
        return Result.Ok(_totp.OtpAuthUri(secret, user.Email) + "\n" + secret);
    }
}
/// <summary>
/// Enables TOTP when the code matches the pending secret.
/// </summary>
public sealed class ConfirmTotpHandler : ICommandHandler<ConfirmTotpCommand, Result<TotpConfirmationDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector _secrets;
    private readonly ITotp _totp;
    private readonly IAuditWriter? _auditWriter;

    public ConfirmTotpHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets, ITotp totp, IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _totp = totp;
        _auditWriter = auditWriter;
    }

    public async Task<Result<TotpConfirmationDto>> HandleAsync(ConfirmTotpCommand command, CancellationToken ct = default)
    {
        var globalTenant = new TenantContext();
        globalTenant.Assign(null, null, unscoped: true);
        await using var db = _dbFactory.CreateDbContext(globalTenant);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.Request.UserId, ct);
        if (user is null || string.IsNullOrEmpty(user.TotpSecretCipher))
            return Result.Failure<TotpConfirmationDto>("Authenticator is not started.");
        string secret;
        try { secret = _secrets.Open(user.TotpSecretCipher); }
        catch (CryptographicException) { return Result.Failure<TotpConfirmationDto>("Authenticator secret cannot be read."); }
        if (!_totp.Verify(secret, command.Request.Code, DateTime.UtcNow))
            return Result.Failure<TotpConfirmationDto>("Code is not valid.");

        var recoveryCodes = TotpRecoveryCodeGenerator.GenerateCodes();
        user.SetTotpRecoveryCodeHashes(recoveryCodes.Select(TotpRecoveryCodeGenerator.HashCode));
        user.ConfirmTotp();
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None), "account.mfa.enabled", "User", user.Id.ToString(), null, null,
            detailsJson: """{"newState":"Enabled"}"""));
        await db.SaveChangesAsync(ct);
        return Result.Ok(new TotpConfirmationDto(recoveryCodes));
    }
}

/// <summary>
/// Replaces an operator's one-time recovery codes after confirming the current authenticator.
/// The plaintext codes are returned once; only SHA-256 hashes are persisted.
/// </summary>
public sealed class RotateOperatorTotpRecoveryCodesHandler(
    IBeaconDbFactory dbFactory,
    ISecretProtector secrets,
    ITotp totp,
    IAuditWriter? auditWriter = null)
{
    public async Task<Result<TotpRecoveryCodesDto>> HandleAsync(
        RotateOperatorTotpRecoveryCodesCommand command, CancellationToken ct = default)
    {
        var actor = command.Actor;
        if (!actor.HasPermission(OperatorPermission.OrganizationsRead))
            return Result.Failure<TotpRecoveryCodesDto>("Operator access is required.", ErrorKind.Forbidden);

        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        await using var db = dbFactory.CreateDbContext(tenant);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await OperatorTotpRecovery.LockOperatorRowAsync(db, transaction, actor.UserId, ct);

        var user = await db.Users.FirstOrDefaultAsync(item => item.Id == actor.UserId, ct);
        if (!OperatorTotpRecovery.IsActiveOperator(user) || !user!.TotpEnabled || string.IsNullOrWhiteSpace(user.TotpSecretCipher))
            return Result.Failure<TotpRecoveryCodesDto>("Operator authenticator is not available.", ErrorKind.Forbidden);
        if (!secrets.TryOpen(user.TotpSecretCipher, out var secret))
            return Result.Failure<TotpRecoveryCodesDto>("Authenticator secret cannot be read.");
        if (!totp.Verify(secret, command.Code ?? string.Empty, DateTime.UtcNow))
            return Result.Failure<TotpRecoveryCodesDto>("Authenticator code is not valid.", ErrorKind.Unauthorized);

        var codes = TotpRecoveryCodeGenerator.GenerateCodes();
        user.SetTotpRecoveryCodeHashes(codes.Select(TotpRecoveryCodeGenerator.HashCode));
        auditWriter?.Stage(db, AuditEventFactory.ForActor(
            OperatorTotpRecovery.OperatorAuditActor(actor), "account.mfa.recovery_codes_rotated", "User", user.Id.ToString(), null, null,
            detailsJson: """{"newState":"Available"}"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Ok(new TotpRecoveryCodesDto(codes));
    }
}

/// <summary>
/// Uses one recovery code to disable a lost authenticator. The code is consumed under a row lock,
/// all remaining recovery codes are revoked, and every existing session is invalidated.
/// </summary>
public sealed class RecoverOperatorTotpHandler(
    IBeaconDbFactory dbFactory,
    IAuditWriter? auditWriter = null,
    IUserSessionVersionCache? sessionVersions = null)
{
    public async Task<Result> HandleAsync(RecoverOperatorTotpCommand command, CancellationToken ct = default)
    {
        var actor = command.Actor;
        if (!actor.HasPermission(OperatorPermission.OrganizationsRead))
            return Result.Forbidden("Operator access is required.");

        var hash = TotpRecoveryCodeGenerator.HashCodeOrNull(command.RecoveryCode);
        if (hash is null)
            return Result.Failure("Recovery code is invalid.", ErrorKind.Unauthorized);

        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        await using var db = dbFactory.CreateDbContext(tenant);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await OperatorTotpRecovery.LockOperatorRowAsync(db, transaction, actor.UserId, ct);

        var user = await db.Users.FirstOrDefaultAsync(item => item.Id == actor.UserId, ct);
        if (!OperatorTotpRecovery.IsActiveOperator(user) || !user!.TotpEnabled
            || !user.TryConsumeTotpRecoveryCodeHash(hash))
            return Result.Failure("Recovery code is invalid or has already been used.", ErrorKind.Unauthorized);

        user.ClearTotp();
        user.BumpSessionVersion();
        auditWriter?.Stage(db, AuditEventFactory.ForActor(
            OperatorTotpRecovery.OperatorAuditActor(actor), "account.mfa.recovery_code_used", "User", user.Id.ToString(), null, null,
            detailsJson: """{"resultCode":"recovery_code_used"}"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        sessionVersions?.Invalidate(user.Id);
        return Result.Ok();
    }
}

internal static class TotpRecoveryCodeGenerator
{
    private const int CodeCount = 10;
    private const int RandomBytes = 12;

    public static IReadOnlyList<string> GenerateCodes() =>
        Enumerable.Range(0, CodeCount)
            .Select(_ =>
            {
                var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(RandomBytes));
                return string.Join("-", Enumerable.Range(0, 6).Select(index => value.Substring(index * 4, 4)));
            })
            .ToArray();

    public static string HashCode(string code)
    {
        var normalized = NormalizeOrNull(code)
            ?? throw new ArgumentException("Recovery code is invalid.", nameof(code));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public static string? HashCodeOrNull(string? code)
    {
        var normalized = NormalizeOrNull(code);
        return normalized is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string? NormalizeOrNull(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var normalized = code.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
        return normalized.Length == 24 && normalized.All(char.IsAsciiHexDigit)
            ? normalized
            : null;
    }
}

internal static class OperatorTotpRecovery
{
    public static bool IsActiveOperator(ProjectBeacon.Domain.Entities.Identity.User? user) =>
        user is not null
        && user.DisabledAt is null
        && Enum.TryParse<OperatorRole>(user.OperatorRole, ignoreCase: false, out var role)
        && Enum.IsDefined(role);

    public static ActorContext OperatorAuditActor(AdminActor actor) =>
        new(ActorType.Human, actor.UserId, false, null, null, null, null, ApiTokenCapability.None);

    public static async Task LockOperatorRowAsync(
        IBeaconDb db, IDbContextTransaction transaction, Guid userId, CancellationToken ct)
    {
        if (!string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return;

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """SELECT "Id" FROM "Users" WHERE "Id" = @user_id FOR UPDATE""";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "user_id";
        parameter.Value = userId;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(ct);
    }
}

/// <summary>
/// Turns TOTP off when the code matches. Fails if it is not on.
/// </summary>
public sealed class DisableTotpHandler : ICommandHandler<DisableTotpCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector _secrets;
    private readonly ITotp _totp;
    private readonly IAuditWriter? _auditWriter;

    public DisableTotpHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets, ITotp totp, IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _totp = totp;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(DisableTotpCommand command, CancellationToken ct = default)
    {
        var globalTenant = new TenantContext();
        globalTenant.Assign(null, null, unscoped: true);
        await using var db = _dbFactory.CreateDbContext(globalTenant);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.Request.UserId, ct);
        if (user is null || !user.TotpEnabled)
            return Result.Failure("Authenticator is not on.");
        var secret = _secrets.Open(user.TotpSecretCipher);
        if (!_totp.Verify(secret, command.Request.Code, DateTime.UtcNow))
            return Result.Failure("Code is not valid.");
        user.ClearTotp();
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None), "account.mfa.disabled", "User", user.Id.ToString(), null, null,
            detailsJson: """{"newState":"Disabled"}"""));
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
/// <summary>
/// Allows the call when TOTP is off or the code matches. Fails when TOTP is required and not configured.
/// </summary>
public static class TotpGate
{
    public static async Task<Result> RequireAsync(
        IBeaconDb db, Guid userId, string? code, ISecretProtector? secrets, ITotp? totp, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.TotpEnabled)
            return Result.Ok();
        if (string.IsNullOrEmpty(user.TotpSecretCipher) || secrets is null || totp is null)
            return Result.Failure("Authenticator is not configured.");
        if (!secrets.TryOpen(user.TotpSecretCipher, out var secret))
            return Result.Failure("Authenticator secret cannot be read.");
        return totp.Verify(secret, code ?? "", DateTime.UtcNow)
            ? Result.Ok()
            : Result.Failure("Authenticator code is required.");
    }
}
