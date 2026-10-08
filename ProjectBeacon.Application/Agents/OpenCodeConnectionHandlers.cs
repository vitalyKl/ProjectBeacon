namespace ProjectBeacon.Application.Agents;

using System.Security.Cryptography;
using Application.Common;
using Application.Devices;
using Application.Security;
using Domain.Entities.Agents;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// An OpenCode provider connection without the API key. HasApiKey is true when one is stored.
/// </summary>
public record OpenCodeConnectionDto(Guid Id, string ProviderId, string ModelId, string BaseUrl, bool HasApiKey);
/// <summary>
/// The same connection including the API key. Returned only to the enrolled device.
/// </summary>
public record OpenCodeConnectionSecretDto(string ProviderId, string ModelId, string BaseUrl, string? ApiKey);
/// <summary>
/// Provider, model, optional base URL, and optional API key for one user.
/// </summary>
public record SaveOpenCodeConnectionRequest(Guid UserId, string ProviderId, string ModelId, string? BaseUrl, string? ApiKey);
/// <summary>
/// Command for save open code connection.
/// </summary>
public record SaveOpenCodeConnectionCommand(SaveOpenCodeConnectionRequest Request) : ICommand<Result<OpenCodeConnectionDto>>;
/// <summary>
/// Fields for delete open code connection.
/// </summary>
public record DeleteOpenCodeConnectionRequest(Guid UserId, Guid Id);
/// <summary>
/// Command for delete open code connection.
/// </summary>
public record DeleteOpenCodeConnectionCommand(DeleteOpenCodeConnectionRequest Request) : ICommand<Result>;
/// <summary>
/// Command for list open code connections.
/// </summary>
public record ListOpenCodeConnectionsCommand(Guid UserId) : ICommand<Result<IList<OpenCodeConnectionDto>>>;
/// <summary>
/// Device asking for its owner's OpenCode connections, including secrets.
/// </summary>
public record DeviceOpenCodeConnectionsCommand(Guid DeviceId) : ICommand<Result<IList<OpenCodeConnectionSecretDto>>>;
/// <summary>
/// Lists the user's OpenCode provider connections without returning stored API keys.
/// </summary>
public sealed class ListOpenCodeConnectionsHandler : ICommandHandler<ListOpenCodeConnectionsCommand, Result<IList<OpenCodeConnectionDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListOpenCodeConnectionsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<OpenCodeConnectionDto>>> HandleAsync(ListOpenCodeConnectionsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var rows = await db.OpenCodeConnections.Where(c => c.UserId == command.UserId).OrderBy(c => c.ProviderId).ToListAsync(ct);
        return Result.Ok<IList<OpenCodeConnectionDto>>(rows.Select(Map).ToList());
    }

    internal static OpenCodeConnectionDto Map(OpenCodeConnection row) =>
        new(row.Id, row.ProviderId, row.ModelId, row.BaseUrl, !string.IsNullOrEmpty(row.ApiKeyCipher));
}
/// <summary>
/// Saves a provider and model connection for the user. Provider id must be a single token.
/// </summary>
public sealed class SaveOpenCodeConnectionHandler : ICommandHandler<SaveOpenCodeConnectionCommand, Result<OpenCodeConnectionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly NudgeOpenCodeHandler _nudge;

    private readonly ISecretProtector _secrets;

    public SaveOpenCodeConnectionHandler(IBeaconDbFactory dbFactory, NudgeOpenCodeHandler nudge, ISecretProtector secrets)
    {
        _dbFactory = dbFactory;
        _nudge = nudge;
        _secrets = secrets;
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
        var cipher = string.IsNullOrWhiteSpace(request.ApiKey) ? null : _secrets.Seal(request.ApiKey.Trim(), _secrets.KeyMaterial());
        if (existing is null)
        {
            existing = OpenCodeConnection.Create(request.UserId, provider, model, request.BaseUrl, cipher ?? "");
            db.OpenCodeConnections.Add(existing);
        }
        else
        {
            existing.Update(model, request.BaseUrl, cipher);
        }
        await DesiredState.BumpUserAsync(db, request.UserId, ct);
        await db.SaveChangesAsync(ct);
        await _nudge.HandleAsync(new NudgeOpenCodeCommand(request.UserId), ct);
        return Result.Ok(ListOpenCodeConnectionsHandler.Map(existing));
    }
}
/// <summary>
/// Deletes one of the user's OpenCode connections.
/// </summary>
public sealed class DeleteOpenCodeConnectionHandler : ICommandHandler<DeleteOpenCodeConnectionCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly NudgeOpenCodeHandler _nudge;

    public DeleteOpenCodeConnectionHandler(IBeaconDbFactory dbFactory, NudgeOpenCodeHandler nudge)
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
        await DesiredState.BumpUserAsync(db, command.Request.UserId, ct);
        await db.SaveChangesAsync(ct);
        await _nudge.HandleAsync(new NudgeOpenCodeCommand(command.Request.UserId), ct);
        return Result.Ok();
    }
}
/// <summary>
/// User whose online devices should reapply OpenCode connections.
/// </summary>
public record NudgeOpenCodeCommand(Guid UserId) : ICommand<Result>;
/// <summary>
/// Enqueues ConfigureOpenCode on each of the user's online devices.
/// </summary>
public sealed class NudgeOpenCodeHandler : ICommandHandler<NudgeOpenCodeCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;

    public NudgeOpenCodeHandler(IBeaconDbFactory dbFactory, EnqueueCommandHandler enqueue)
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
/// <summary>
/// Returns the device owner's OpenCode connections, including secrets, for the enrolled device. Fails when the device is missing.
/// </summary>
public sealed class DeviceOpenCodeConnectionsHandler : ICommandHandler<DeviceOpenCodeConnectionsCommand, Result<IList<OpenCodeConnectionSecretDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    private readonly ISecretProtector _secrets;

    public DeviceOpenCodeConnectionsHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
    }

    public async Task<Result<IList<OpenCodeConnectionSecretDto>>> HandleAsync(DeviceOpenCodeConnectionsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.DeviceId && d.RevokedAt == null, ct);
        if (device is null)
            return Result.Failure<IList<OpenCodeConnectionSecretDto>>("Device not found.");
        var rows = await db.OpenCodeConnections.Where(c => c.UserId == device.UserId).OrderBy(c => c.ProviderId).ToListAsync(ct);
        var key = _secrets.KeyMaterial();
        var list = rows.Select(row =>
        {
            string? api = null;
            if (!string.IsNullOrEmpty(row.ApiKeyCipher))
            {
                try { api = _secrets.Open(row.ApiKeyCipher, key); }
                catch (CryptographicException) { api = null; }
            }
            return new OpenCodeConnectionSecretDto(row.ProviderId, row.ModelId, row.BaseUrl, api);
        }).ToList();
        return Result.Ok<IList<OpenCodeConnectionSecretDto>>(list);
    }
}
