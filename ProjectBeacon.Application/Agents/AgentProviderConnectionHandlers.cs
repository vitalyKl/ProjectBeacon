namespace ProjectBeacon.Application.Agents;

using System.Security.Cryptography;
using Application.Common;
using Application.Devices;
using Application.Security;
using Domain.Entities.Agents;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// A provider connection without the API key. HasApiKey is true when one is stored.
/// </summary>
public record AgentProviderConnectionDto(Guid Id, string ProviderId, string ModelId, string BaseUrl, bool HasApiKey);
/// <summary>
/// The same connection including the API key. Returned only to the enrolled device.
/// </summary>
public record AgentProviderConnectionSecretDto(string ProviderId, string ModelId, string BaseUrl, string? ApiKey);
/// <summary>
/// Provider, model, optional base URL, and optional API key for one user.
/// </summary>
public record SaveAgentProviderConnectionRequest(Guid UserId, string ProviderId, string ModelId, string? BaseUrl, string? ApiKey);
/// <summary>
/// Command for save open code connection.
/// </summary>
public record SaveAgentProviderConnectionCommand(SaveAgentProviderConnectionRequest Request) : ICommand<Result<AgentProviderConnectionDto>>;
/// <summary>
/// Fields for delete open code connection.
/// </summary>
public record DeleteAgentProviderConnectionRequest(Guid UserId, Guid Id);
/// <summary>
/// Command for delete open code connection.
/// </summary>
public record DeleteAgentProviderConnectionCommand(DeleteAgentProviderConnectionRequest Request) : ICommand<Result>;
/// <summary>
/// Command for list open code connections.
/// </summary>
public record ListAgentProviderConnectionsCommand(Guid UserId) : ICommand<Result<IList<AgentProviderConnectionDto>>>;
/// <summary>
/// Device asking for its owner's provider connections, including secrets.
/// </summary>
public record DeviceAgentProviderConnectionsCommand(Guid DeviceId) : ICommand<Result<IList<AgentProviderConnectionSecretDto>>>;
/// <summary>
/// Lists the user's provider connections without returning stored API keys.
/// </summary>
public sealed class ListAgentProviderConnectionsHandler : ICommandHandler<ListAgentProviderConnectionsCommand, Result<IList<AgentProviderConnectionDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListAgentProviderConnectionsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<AgentProviderConnectionDto>>> HandleAsync(ListAgentProviderConnectionsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var rows = await db.AgentProviderConnections.Where(c => c.UserId == command.UserId).OrderBy(c => c.ProviderId).ToListAsync(ct);
        return Result.Ok<IList<AgentProviderConnectionDto>>(rows.Select(Map).ToList());
    }

    internal static AgentProviderConnectionDto Map(AgentProviderConnection row) =>
        new(row.Id, row.ProviderId, row.ModelId, row.BaseUrl, !string.IsNullOrEmpty(row.ApiKeyCipher));
}
/// <summary>
/// Saves a provider and model connection for the user. Provider id must be a single token.
/// </summary>
public sealed class SaveAgentProviderConnectionHandler : ICommandHandler<SaveAgentProviderConnectionCommand, Result<AgentProviderConnectionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector _secrets;

    public SaveAgentProviderConnectionHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
    }

    public async Task<Result<AgentProviderConnectionDto>> HandleAsync(SaveAgentProviderConnectionCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        var provider = request.ProviderId?.Trim().ToLowerInvariant() ?? "";
        var model = request.ModelId?.Trim() ?? "";
        if (request.UserId == Guid.Empty)
            return Result.Failure<AgentProviderConnectionDto>("Account is not resolved.");
        if (provider.Length is 0 or > 80 || model.Length is 0 or > 120)
            return Result.Failure<AgentProviderConnectionDto>("Provider and model are required.");
        if (provider.Contains('/') || provider.Any(c => char.IsWhiteSpace(c)))
            return Result.Failure<AgentProviderConnectionDto>("Provider id is a single token, like xai or openai.");

        await using var db = _dbFactory.CreateDbContext();
        var existing = await db.AgentProviderConnections.FirstOrDefaultAsync(c => c.UserId == request.UserId && c.ProviderId == provider, ct);
        var cipher = string.IsNullOrWhiteSpace(request.ApiKey) ? null : _secrets.Seal(request.ApiKey.Trim());
        if (existing is null)
        {
            existing = AgentProviderConnection.Create(request.UserId, provider, model, request.BaseUrl, cipher ?? "");
            db.AgentProviderConnections.Add(existing);
        }
        else
        {
            existing.Update(model, request.BaseUrl, cipher);
        }
        await DesiredState.BumpUserAsync(db, request.UserId, ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ListAgentProviderConnectionsHandler.Map(existing));
    }
}
/// <summary>
/// Deletes one of the user's provider connections.
/// </summary>
public sealed class DeleteAgentProviderConnectionHandler : ICommandHandler<DeleteAgentProviderConnectionCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public DeleteAgentProviderConnectionHandler(IBeaconDbFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<Result> HandleAsync(DeleteAgentProviderConnectionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.AgentProviderConnections.FirstOrDefaultAsync(c => c.Id == command.Request.Id && c.UserId == command.Request.UserId, ct);
        if (row is null)
            return Result.Failure("Connection not found.");
        db.AgentProviderConnections.Remove(row);
        await DesiredState.BumpUserAsync(db, command.Request.UserId, ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
/// <summary>
/// Returns the device owner's provider connections, including secrets, for the enrolled device. Fails when the device is missing.
/// </summary>
public sealed class DeviceAgentProviderConnectionsHandler : ICommandHandler<DeviceAgentProviderConnectionsCommand, Result<IList<AgentProviderConnectionSecretDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    private readonly ISecretProtector _secrets;

    public DeviceAgentProviderConnectionsHandler(IBeaconDbFactory dbFactory, ISecretProtector secrets)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
    }

    public async Task<Result<IList<AgentProviderConnectionSecretDto>>> HandleAsync(DeviceAgentProviderConnectionsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.DeviceId && d.RevokedAt == null, ct);
        if (device is null)
            return Result.Failure<IList<AgentProviderConnectionSecretDto>>("Device not found.");
        var rows = await db.AgentProviderConnections.Where(c => c.UserId == device.UserId).OrderBy(c => c.ProviderId).ToListAsync(ct);
        var list = rows.Select(row =>
        {
            string? api = null;
            if (!string.IsNullOrEmpty(row.ApiKeyCipher))
            {
                try { api = _secrets.Open(row.ApiKeyCipher); }
                catch (CryptographicException) { api = null; }
            }
            return new AgentProviderConnectionSecretDto(row.ProviderId, row.ModelId, row.BaseUrl, api);
        }).ToList();
        return Result.Ok<IList<AgentProviderConnectionSecretDto>>(list);
    }
}
