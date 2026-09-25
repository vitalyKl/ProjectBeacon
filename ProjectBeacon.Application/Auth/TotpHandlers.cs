namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

public record TotpStatusDto(bool Enabled, bool Pending);

public record GetTotpStatusCommand(Guid UserId) : ICommand<Result<TotpStatusDto>>;

public record BeginTotpCommand(Guid UserId) : ICommand<Result<string>>;

public record ConfirmTotpRequest(Guid UserId, string Code);

public record ConfirmTotpCommand(ConfirmTotpRequest Request) : ICommand<Result>;

public record DisableTotpRequest(Guid UserId, string Code);

public record DisableTotpCommand(DisableTotpRequest Request) : ICommand<Result>;

public sealed class GetTotpStatusHandler : ICommandHandler<GetTotpStatusCommand, Result<TotpStatusDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetTotpStatusHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TotpStatusDto>> HandleAsync(GetTotpStatusCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, ct);
        if (user is null)
            return Result.Failure<TotpStatusDto>("Account is not resolved.");
        return Result.Ok(new TotpStatusDto(user.TotpEnabled, !user.TotpEnabled && !string.IsNullOrEmpty(user.TotpSecretCipher)));
    }
}

public sealed class BeginTotpHandler : ICommandHandler<BeginTotpCommand, Result<string>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public BeginTotpHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<string>> HandleAsync(BeginTotpCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, ct);
        if (user is null)
            return Result.Failure<string>("Account is not resolved.");
        var secret = Totp.GenerateSecret();
        user.BeginTotp(SecretBox.Seal(secret, SecretBox.KeyMaterial()));
        await db.SaveChangesAsync(ct);
        return Result.Ok(Totp.OtpAuthUri(secret, user.Email) + "\n" + secret);
    }
}

public sealed class ConfirmTotpHandler : ICommandHandler<ConfirmTotpCommand, Result>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ConfirmTotpHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(ConfirmTotpCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.Request.UserId, ct);
        if (user is null || string.IsNullOrEmpty(user.TotpSecretCipher))
            return Result.Failure("Authenticator is not started.");
        string secret;
        try { secret = SecretBox.Open(user.TotpSecretCipher, SecretBox.KeyMaterial()); }
        catch (System.Security.Cryptography.CryptographicException) { return Result.Failure("Authenticator secret cannot be read."); }
        if (!Totp.Verify(secret, command.Request.Code, DateTime.UtcNow))
            return Result.Failure("Code is not valid.");
        user.ConfirmTotp();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public sealed class DisableTotpHandler : ICommandHandler<DisableTotpCommand, Result>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DisableTotpHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DisableTotpCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.Request.UserId, ct);
        if (user is null || !user.TotpEnabled)
            return Result.Failure("Authenticator is not on.");
        var secret = SecretBox.Open(user.TotpSecretCipher, SecretBox.KeyMaterial());
        if (!Totp.Verify(secret, command.Request.Code, DateTime.UtcNow))
            return Result.Failure("Code is not valid.");
        user.ClearTotp();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public static class TotpGate
{
    public static async Task<Result> RequireAsync(BeaconDbContext db, Guid userId, string? code, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.TotpEnabled)
            return Result.Ok();
        if (string.IsNullOrEmpty(user.TotpSecretCipher))
            return Result.Failure("Authenticator is not configured.");
        var secret = SecretBox.Open(user.TotpSecretCipher, SecretBox.KeyMaterial());
        return Totp.Verify(secret, code ?? "", DateTime.UtcNow)
            ? Result.Ok()
            : Result.Failure("Authenticator code is required.");
    }
}
