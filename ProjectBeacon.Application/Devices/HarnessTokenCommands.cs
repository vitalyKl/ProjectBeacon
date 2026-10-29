namespace ProjectBeacon.Application.Devices;

using System.Security.Cryptography;
using System.Text;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Identity;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Issues a one-shot API credential for a currently running workstation chat command.
/// The raw credential is returned once only; the database stores its hash.
/// </summary>
public sealed record IssueHarnessTokenCommand(Guid CommandId, int Generation, ActorContext Actor)
    : ICommand<Result<HarnessTokenDto>>;

/// <summary>Ephemeral credential returned only to the authenticated workstation.</summary>
public sealed record HarnessTokenDto(string Token, Guid ProjectId, Guid CommandId, int Generation, DateTime ExpiresAt);

public sealed class IssueHarnessTokenHandler(IBeaconDbFactory dbFactory)
    : ICommandHandler<IssueHarnessTokenCommand, Result<HarnessTokenDto>>
{
    private const ApiTokenCapability HarnessCapabilities =
        ApiTokenCapability.ContextRead | ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite;

    public async Task<Result<HarnessTokenDto>> HandleAsync(
        IssueHarnessTokenCommand command,
        CancellationToken ct = default)
    {
        var actor = command.Actor;
        if (!actor.IsDevice || actor.DeviceId is not { } deviceId || deviceId == Guid.Empty)
            return Result.Failure<HarnessTokenDto>("A device token is required.", ErrorKind.Forbidden);
        if (command.Generation < 1)
            return Result.Failure<HarnessTokenDto>("A valid command generation is required.", ErrorKind.Conflict);

        await using var db = dbFactory.CreateDbContext();
        var now = DateTime.UtcNow;

        var device = await db.DaemonDevices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null || device.IsRevoked || !device.IsOnline(now))
            return Result.Failure<HarnessTokenDto>("The workstation is offline or revoked.", ErrorKind.Forbidden);

        var work = await db.WorkstationCommands.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == command.CommandId && c.DeviceId == deviceId, ct);
        if (work is null
            || work.Kind != WorkstationCommandKind.ChatPrompt
            || work.ProjectId is not { } projectId
            || work.Status != WorkstationCommandStatus.Running
            || work.StartedAt is null
            || work.LeaseExpiresAt is not { } leaseExpiresAt
            || leaseExpiresAt <= now)
        {
            return Result.Failure<HarnessTokenDto>("The chat command is not running under an active lease.", ErrorKind.Forbidden);
        }

        if (work.Attempts + 1 != command.Generation)
            return Result.Failure<HarnessTokenDto>("The chat command generation is stale.", ErrorKind.Conflict);

        if (!string.Equals(work.ClaimInstanceId, device.ActiveInstanceId, StringComparison.Ordinal))
            return Result.Failure<HarnessTokenDto>("The command belongs to a different workstation process instance.", ErrorKind.Forbidden);

        // A renewed claim must never inherit a credential from an earlier attempt.
        var previous = await db.ApiTokens.IgnoreQueryFilters()
            .Where(t => t.ClientKind == ApiTokenClientKind.Harness
                && t.CommandId == work.Id
                && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in previous)
            token.Revoke();

        var rawToken = "bcn_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        var tokenPrefix = rawToken[..Math.Min(8, rawToken.Length)];
        var tokenRow = ApiToken.Create(
            $"harness:{work.Id:N}",
            tokenHash,
            tokenPrefix,
            projectId,
            HarnessCapabilities,
            leaseExpiresAt,
            device.UserId,
            ApiTokenClientKind.Harness,
            deviceId,
            work.Id);

        db.ApiTokens.Add(tokenRow);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new HarnessTokenDto(rawToken, projectId, work.Id, command.Generation, leaseExpiresAt));
    }
}
