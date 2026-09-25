namespace ProjectBeacon.Application.Agents;

using System.Security.Cryptography;
using Application.Common;
using Application.Devices;
using Domain.Entities.Agents;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

public record OpenCodeConnectionDto(Guid Id, string ProviderId, string ModelId, string BaseUrl, bool HasApiKey);

public record OpenCodeConnectionSecretDto(string ProviderId, string ModelId, string BaseUrl, string? ApiKey);

public record SaveOpenCodeConnectionRequest(Guid UserId, string ProviderId, string ModelId, string? BaseUrl, string? ApiKey);

public record SaveOpenCodeConnectionCommand(SaveOpenCodeConnectionRequest Request) : ICommand<Result<OpenCodeConnectionDto>>;

public record DeleteOpenCodeConnectionRequest(Guid UserId, Guid Id);

public record DeleteOpenCodeConnectionCommand(DeleteOpenCodeConnectionRequest Request) : ICommand<Result>;

public record ListOpenCodeConnectionsCommand(Guid UserId) : ICommand<Result<IList<OpenCodeConnectionDto>>>;

public record DeviceOpenCodeConnectionsCommand(Guid DeviceId) : ICommand<Result<IList<OpenCodeConnectionSecretDto>>>;

public sealed class ListOpenCodeConnectionsHandler : ICommandHandler<ListOpenCodeConnectionsCommand, Result<IList<OpenCodeConnectionDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListOpenCodeConnectionsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<OpenCodeConnectionDto>>> HandleAsync(ListOpenCodeConnectionsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var rows = await db.OpenCodeConnections.Where(c => c.UserId == command.UserId).OrderBy(c => c.ProviderId).ToListAsync(ct);
        return Result.Ok<IList<OpenCodeConnectionDto>>(rows.Select(Map).ToList());
    }

    internal static OpenCodeConnectionDto Map(OpenCodeConnection row) =>
        new(row.Id, row.ProviderId, row.ModelId, row.BaseUrl, !string.IsNullOrEmpty(row.ApiKeyCipher));
}

public sealed class SaveOpenCodeConnectionHandler : ICommandHandler<SaveOpenCodeConnectionCommand, Result<OpenCodeConnectionDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly NudgeOpenCodeHandler _nudge;

    public SaveOpenCodeConnectionHandler(IDbContextFactory<BeaconDbContext> dbFactory, NudgeOpenCodeHandler nudge)
    {
        _dbFactory = dbFactory;
        _nudge = nudge;
    }

    public async Task<Result<OpenCodeConnectionDto>> HandleAsync(SaveOpenCodeConnectionCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        var provider = request.ProviderId?.Trim().ToLowerInvariant() ?? "";
        var model = request.ModelId?.Trim() ?? "";
        if (request.UserId == Guid.Empty)
            return Result.Failure<OpenCodeConnectionDto>("Account is not resolved.");
        if (provider.Length is 0 or > 80 || model.Length is 0 or > 120)
            return Result.Failure<OpenCodeConnectionDto>("Provider and model are required.");
        if (provider.Contains('/') || provider.Any(c => char.IsWhiteSpace(c)))
            return Result.Failure<OpenCodeConnectionDto>("Provider id is a single token, like xai or openai.");

        await using var db = _dbFactory.CreateDbContext();
        var existing = await db.OpenCodeConnections.FirstOrDefaultAsync(c => c.UserId == request.UserId && c.ProviderId == provider, ct);
        var cipher = string.IsNullOrWhiteSpace(request.ApiKey) ? null : SecretBox.Seal(request.ApiKey.Trim(), SecretBox.KeyMaterial());
        if (existing is null)
        {
            existing = OpenCodeConnection.Create(request.UserId, provider, model, request.BaseUrl, cipher ?? "");
            db.OpenCodeConnections.Add(existing);
        }
        else
        {
            existing.Update(model, request.BaseUrl, cipher);
        }
        await db.SaveChangesAsync(ct);
        await _nudge.HandleAsync(new NudgeOpenCodeCommand(request.UserId), ct);
        return Result.Ok(ListOpenCodeConnectionsHandler.Map(existing));
    }
}

public sealed class DeleteOpenCodeConnectionHandler : ICommandHandler<DeleteOpenCodeConnectionCommand, Result>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly NudgeOpenCodeHandler _nudge;

    public DeleteOpenCodeConnectionHandler(IDbContextFactory<BeaconDbContext> dbFactory, NudgeOpenCodeHandler nudge)
    {
        _dbFactory = dbFactory;
        _nudge = nudge;
    }

    public async Task<Result> HandleAsync(DeleteOpenCodeConnectionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.OpenCodeConnections.FirstOrDefaultAsync(c => c.Id == command.Request.Id && c.UserId == command.Request.UserId, ct);
        if (row is null)
            return Result.Failure("Connection not found.");
        db.OpenCodeConnections.Remove(row);
        await db.SaveChangesAsync(ct);
        await _nudge.HandleAsync(new NudgeOpenCodeCommand(command.Request.UserId), ct);
        return Result.Ok();
    }
}

public record NudgeOpenCodeCommand(Guid UserId) : ICommand<Result>;

public sealed class NudgeOpenCodeHandler : ICommandHandler<NudgeOpenCodeCommand, Result>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;

    public NudgeOpenCodeHandler(IDbContextFactory<BeaconDbContext> dbFactory, EnqueueCommandHandler enqueue)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
    }

    public async Task<Result> HandleAsync(NudgeOpenCodeCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var now = DateTime.UtcNow;
        var devices = await db.DaemonDevices.Where(d => d.UserId == command.UserId && d.RevokedAt == null).ToListAsync(ct);
        foreach (var device in devices.Where(d => d.IsOnline(now)))
        {
            await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
                device.Id, command.UserId, WorkstationCommandKind.ConfigureOpenCode, "{}", null)), ct);
        }
        return Result.Ok();
    }
}

public sealed class DeviceOpenCodeConnectionsHandler : ICommandHandler<DeviceOpenCodeConnectionsCommand, Result<IList<OpenCodeConnectionSecretDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DeviceOpenCodeConnectionsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<OpenCodeConnectionSecretDto>>> HandleAsync(DeviceOpenCodeConnectionsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.DeviceId && d.RevokedAt == null, ct);
        if (device is null)
            return Result.Failure<IList<OpenCodeConnectionSecretDto>>("Device not found.");
        var rows = await db.OpenCodeConnections.Where(c => c.UserId == device.UserId).OrderBy(c => c.ProviderId).ToListAsync(ct);
        var key = SecretBox.KeyMaterial();
        var list = rows.Select(row =>
        {
            string? api = null;
            if (!string.IsNullOrEmpty(row.ApiKeyCipher))
            {
                try { api = SecretBox.Open(row.ApiKeyCipher, key); }
                catch (CryptographicException) { api = null; }
            }
            return new OpenCodeConnectionSecretDto(row.ProviderId, row.ModelId, row.BaseUrl, api);
        }).ToList();
        return Result.Ok<IList<OpenCodeConnectionSecretDto>>(list);
    }
}
