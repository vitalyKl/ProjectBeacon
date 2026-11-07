namespace ProjectBeacon.Application.Agents;

using Application.Common;
using Application.Persistence;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// User whose selected chat backend should be returned.
/// </summary>
public record GetChatModelCommand(Guid UserId) : ICommand<Result<Guid?>>;
/// <summary>
/// User and the chat backend to select. BackendId null clears the selection.
/// </summary>
public record SetChatModelRequest(Guid UserId, Guid? BackendId);
/// <summary>
/// Command for set chat model.
/// </summary>
public record SetChatModelCommand(SetChatModelRequest Request) : ICommand<Result<Guid?>>;
/// <summary>
/// Returns the user's selected chat backend id. An unknown user is an unresolved account.
/// </summary>
public sealed class GetChatModelHandler : ICommandHandler<GetChatModelCommand, Result<Guid?>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetChatModelHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<Guid?>> HandleAsync(GetChatModelCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, ct);
        return user is null
            ? Result.Failure<Guid?>("Account is not resolved.")
            : Result.Ok(user.ChatModelBackendId);
    }
}
/// <summary>
/// Sets or clears the user's chat backend. The backend must belong to that user.
/// </summary>
public sealed class SetChatModelHandler : ICommandHandler<SetChatModelCommand, Result<Guid?>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public SetChatModelHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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
/// <summary>
/// Uses the requested model string when it is set. Otherwise resolves the user's selected provider model id.
/// </summary>
public static class ChatModelSelection
{
    public static async Task<string?> ResolveAsync(IBeaconDb db, Guid userId, string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested.Trim();
        var preferred = await db.Users.Where(u => u.Id == userId).Select(u => u.ChatModelBackendId).FirstOrDefaultAsync(ct);
        if (preferred is not Guid backendId)
            return null;
        var backend = await db.LocalModelBackends
            .Where(b => b.Id == backendId && b.UserId == userId)
            .FirstOrDefaultAsync(ct);
        return backend is null ? null : ResolveBackendModelId(backend.BackendType, backend.Name, backend.ProviderModelId);
    }

    public static string ResolveBackendModelId(ModelBackendType backendType, string name, string providerModelId)
        => backendType == ModelBackendType.LlamaCpp
            ? $"beacon-local/{name.Trim()}"
            : providerModelId.Trim();

    /// <summary>Resolves the configured context window for the exact selected model.</summary>
    public static async Task<int?> ResolveContextLimitAsync(
        IBeaconDb db, Guid userId, string? resolvedModel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resolvedModel))
            return null;

        var backends = await db.LocalModelBackends
            .Where(b => b.UserId == userId)
            .Select(b => new { b.Name, b.BackendType, b.ProviderModelId, b.ContextSize })
            .ToListAsync(ct);

        foreach (var backend in backends)
        {
            if (backend.ContextSize <= 0)
                continue;
            var modelId = ResolveBackendModelId(backend.BackendType, backend.Name, backend.ProviderModelId);
            if (string.Equals(modelId, resolvedModel.Trim(), StringComparison.Ordinal))
                return backend.ContextSize;
        }
        return null;
    }
}
