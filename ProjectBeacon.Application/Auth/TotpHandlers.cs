namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Application.Security;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Whether TOTP is enabled. OtpAuthUri and Secret are set only while setup is still pending.
/// </summary>
public record TotpStatusDto(bool Enabled, bool Pending, string? OtpAuthUri, string? Secret);
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
public record ConfirmTotpCommand(ConfirmTotpRequest Request) : ICommand<Result>;
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
        await using var db = _dbFactory.CreateDbContext();
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
                secret = _secrets.Open(user.TotpSecretCipher, _secrets.KeyMaterial());
                uri = _totp.OtpAuthUri(secret, user.Email);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                pending = false;
            }
        }
        return Result.Ok(new TotpStatusDto(user.TotpEnabled, pending, uri, secret));
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

    public BeginTotpHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets, ITotp totp)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _totp = totp;
    }

    public async Task<Result<string>> HandleAsync(BeginTotpCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, ct);
        if (user is null)
            return Result.Failure<string>("Account is not resolved.");
        var secret = _totp.GenerateSecret();
        user.BeginTotp(_secrets.Seal(secret, _secrets.KeyMaterial()));
        await db.SaveChangesAsync(ct);
        return Result.Ok(_totp.OtpAuthUri(secret, user.Email) + "\n" + secret);
    }
}
/// <summary>
/// Enables TOTP when the code matches the pending secret.
/// </summary>
public sealed class ConfirmTotpHandler : ICommandHandler<ConfirmTotpCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector _secrets;
    private readonly ITotp _totp;

    public ConfirmTotpHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets, ITotp totp)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _totp = totp;
    }

    public async Task<Result> HandleAsync(ConfirmTotpCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.Request.UserId, ct);
        if (user is null || string.IsNullOrEmpty(user.TotpSecretCipher))
            return Result.Failure("Authenticator is not started.");
        string secret;
        try { secret = _secrets.Open(user.TotpSecretCipher, _secrets.KeyMaterial()); }
        catch (System.Security.Cryptography.CryptographicException) { return Result.Failure("Authenticator secret cannot be read."); }
        if (!_totp.Verify(secret, command.Request.Code, DateTime.UtcNow))
            return Result.Failure("Code is not valid.");
        user.ConfirmTotp();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
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

    public DisableTotpHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets, ITotp totp)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _totp = totp;
    }

    public async Task<Result> HandleAsync(DisableTotpCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.Request.UserId, ct);
        if (user is null || !user.TotpEnabled)
            return Result.Failure("Authenticator is not on.");
        var secret = _secrets.Open(user.TotpSecretCipher, _secrets.KeyMaterial());
        if (!_totp.Verify(secret, command.Request.Code, DateTime.UtcNow))
            return Result.Failure("Code is not valid.");
        user.ClearTotp();
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
        var secret = secrets.Open(user.TotpSecretCipher, secrets.KeyMaterial());
        return totp.Verify(secret, code ?? "", DateTime.UtcNow)
            ? Result.Ok()
            : Result.Failure("Authenticator code is required.");
    }
}
