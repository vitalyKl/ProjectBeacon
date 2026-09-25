namespace ProjectBeacon.Application.Auth;

using Application.Agents;
using Application.Common;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record GetChatModelCommand(Guid UserId) : ICommand<Result<Guid?>>;

public record SetChatModelRequest(Guid UserId, Guid? BackendId);

public record SetChatModelCommand(SetChatModelRequest Request) : ICommand<Result<Guid?>>;

public sealed class GetChatModelHandler : ICommandHandler<GetChatModelCommand, Result<Guid?>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetChatModelHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<Guid?>> HandleAsync(GetChatModelCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, ct);
        return user is null
            ? Result.Failure<Guid?>("Account is not resolved.")
            : Result.Ok(user.ChatModelBackendId);
    }
}

public sealed class SetChatModelHandler : ICommandHandler<SetChatModelCommand, Result<Guid?>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public SetChatModelHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<Guid?>> HandleAsync(SetChatModelCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.Request.UserId, ct);
        if (user is null)
            return Result.Failure<Guid?>("Account is not resolved.");
        if (command.Request.BackendId is { } backendId)
        {
            var owned = await db.LocalModelBackends.AnyAsync(b => b.Id == backendId && b.UserId == user.Id, ct);
            if (!owned)
                return Result.Failure<Guid?>("Model backend not found.");
        }
        user.SetChatModel(command.Request.BackendId);
        await db.SaveChangesAsync(ct);
        return Result.Ok(user.ChatModelBackendId);
    }
}

public static class ChatModelSelection
{
    public static async Task<string?> ResolveAsync(BeaconDbContext db, Guid userId, string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested.Trim();
        var preferred = await db.Users.Where(u => u.Id == userId).Select(u => u.ChatModelBackendId).FirstOrDefaultAsync(ct);
        if (preferred is not Guid backendId)
            return null;
        var name = await db.LocalModelBackends.Where(b => b.Id == backendId && b.UserId == userId).Select(b => b.Name).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? null : $"{OpencodePayload.ProviderId}/{OpencodePayload.ModelKey(name)}";
    }
}
