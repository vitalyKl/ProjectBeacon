namespace ProjectBeacon.Application.Agents;

using Application.Common;
using Domain.Enums;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.EntityFrameworkCore;

public static class ModelProjectScope
{
    public static bool TryGet(BeaconDbContext db, out Guid projectId)
    {
        projectId = Guid.Empty;
        var scope = db.FilterProjectId;
        if (scope is null || scope == Guid.Empty)
            return false;
        projectId = scope.Value;
        return true;
    }
}

internal static class ModelBackendMappers
{
    public static LocalModelBackendDto ToDto(LocalModelBackend backend) =>
        new(backend.Id, backend.Name, backend.BackendType, backend.LaunchCommand,
            backend.ContextSize, backend.Ttl, backend.ExtraFlags, backend.ProjectId, backend.UpdatedAt, backend.Concurrent);

    public static RoleBindingDto ToDto(RoleBinding binding) =>
        new(binding.Id, binding.Role, binding.ModelBackendId, binding.ProjectId);
}

public sealed class UpsertLocalModelBackendHandler : ICommandHandler<UpsertLocalModelBackendCommand, Result<LocalModelBackendDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public UpsertLocalModelBackendHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<LocalModelBackendDto>> HandleAsync(UpsertLocalModelBackendCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 200)
            return Result.Failure<LocalModelBackendDto>("Name is required (max 200 characters).");
        if (string.IsNullOrWhiteSpace(request.LaunchCommand) || request.LaunchCommand.Length > 1000)
            return Result.Failure<LocalModelBackendDto>("LaunchCommand is required (max 1000 characters).");
        if (request.ContextSize < 0 || request.Ttl < 0)
            return Result.Failure<LocalModelBackendDto>("ContextSize and Ttl must be non-negative.");

        await using var db = _dbFactory.CreateDbContext();
        if (!ModelProjectScope.TryGet(db, out var projectId))
            return Result.Failure<LocalModelBackendDto>("Project scope is not resolved.");

        if (request.Id is { } id)
        {
            var existing = await db.LocalModelBackends.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (existing is null)
                return Result.Failure<LocalModelBackendDto>("Model backend not found.");
            existing.Update(name, request.BackendType, request.LaunchCommand, request.ContextSize, request.Ttl, request.ExtraFlags, request.Concurrent);
            await db.SaveChangesAsync(ct);
            return Result.Ok(ModelBackendMappers.ToDto(existing));
        }

        var backend = LocalModelBackend.Create(name, request.BackendType, request.LaunchCommand,
            request.ContextSize, request.Ttl, projectId, request.ExtraFlags, request.Concurrent);
        db.LocalModelBackends.Add(backend);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ModelBackendMappers.ToDto(backend));
    }
}

public sealed class DeleteLocalModelBackendHandler : ICommandHandler<DeleteLocalModelBackendCommand, Result>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DeleteLocalModelBackendHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DeleteLocalModelBackendCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (!ModelProjectScope.TryGet(db, out _))
            return Result.Failure("Project scope is not resolved.");

        var backend = await db.LocalModelBackends.FirstOrDefaultAsync(b => b.Id == command.Request.Id, ct);
        if (backend is null)
            return Result.Failure("Model backend not found.");

        var boundRoles = await db.RoleBindings
            .Where(r => r.ModelBackendId == backend.Id)
            .Select(r => r.Role)
            .ToListAsync(ct);
        if (boundRoles.Count > 0)
            return Result.Failure($"Model backend is bound to role(s): {string.Join(", ", boundRoles.Select(r => r.ToString().ToLowerInvariant()))}. Unbind first.");

        db.LocalModelBackends.Remove(backend);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public sealed class SetRoleBindingHandler : ICommandHandler<SetRoleBindingCommand, Result<RoleBindingDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public SetRoleBindingHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<RoleBindingDto>> HandleAsync(SetRoleBindingCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (!ModelProjectScope.TryGet(db, out var projectId))
            return Result.Failure<RoleBindingDto>("Project scope is not resolved.");

        var backendExists = await db.LocalModelBackends.AnyAsync(b => b.Id == command.Request.ModelBackendId, ct);
        if (!backendExists)
            return Result.Failure<RoleBindingDto>("Model backend not found.");

        var binding = await db.RoleBindings.FirstOrDefaultAsync(r => r.Role == command.Request.Role, ct);
        if (binding is null)
        {
            binding = RoleBinding.Create(command.Request.Role, command.Request.ModelBackendId, projectId);
            db.RoleBindings.Add(binding);
        }
        else
        {
            binding.ChangeBackend(command.Request.ModelBackendId);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(ModelBackendMappers.ToDto(binding));
    }
}

public sealed class RemoveRoleBindingHandler : ICommandHandler<RemoveRoleBindingCommand, Result>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public RemoveRoleBindingHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(RemoveRoleBindingCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (!ModelProjectScope.TryGet(db, out _))
            return Result.Failure("Project scope is not resolved.");

        var binding = await db.RoleBindings.FirstOrDefaultAsync(r => r.Role == command.Request.Role, ct);
        if (binding is null)
            return Result.Failure("Role binding not found.");

        db.RoleBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public sealed class GetModelRegistryHandler : ICommandHandler<GetModelRegistryCommand, Result<ModelRegistryDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetModelRegistryHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ModelRegistryDto>> HandleAsync(GetModelRegistryCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (!ModelProjectScope.TryGet(db, out var projectId))
            return Result.Failure<ModelRegistryDto>("Project scope is not resolved.");

        var backends = await db.LocalModelBackends
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
        var bindings = await db.RoleBindings
            .OrderBy(r => r.Role)
            .ToListAsync(ct);

        return Result.Ok(new ModelRegistryDto(
            projectId,
            backends.Select(ModelBackendMappers.ToDto).ToList(),
            bindings.Select(ModelBackendMappers.ToDto).ToList()));
    }
}

public sealed class GetProxyStatusHandler : ICommandHandler<GetProxyStatusCommand, Result<LlamaSwapStatusDto>>
{
    private readonly ILlamaSwapProxy _proxy;

    public GetProxyStatusHandler(ILlamaSwapProxy proxy) => _proxy = proxy;

    public async Task<Result<LlamaSwapStatusDto>> HandleAsync(GetProxyStatusCommand command, CancellationToken ct = default)
    {
        var status = await _proxy.GetStatusAsync(ct);
        return Result.Ok(status);
    }
}

public sealed class ReloadProxyHandler : ICommandHandler<ReloadProxyCommand, Result<LlamaSwapStatusDto>>
{
    private readonly ILlamaSwapProxy _proxy;

    public ReloadProxyHandler(ILlamaSwapProxy proxy) => _proxy = proxy;

    public async Task<Result<LlamaSwapStatusDto>> HandleAsync(ReloadProxyCommand command, CancellationToken ct = default)
    {
        var ok = await _proxy.ReloadAsync(ct);
        var status = await _proxy.GetStatusAsync(ct);
        if (!ok)
            return Result.Failure<LlamaSwapStatusDto>(status.Error ?? "Proxy reload failed.");
        return Result.Ok(status);
    }
}

public sealed class UnloadProxyHandler : ICommandHandler<UnloadProxyCommand, Result<LlamaSwapStatusDto>>
{
    private readonly ILlamaSwapProxy _proxy;

    public UnloadProxyHandler(ILlamaSwapProxy proxy) => _proxy = proxy;

    public async Task<Result<LlamaSwapStatusDto>> HandleAsync(UnloadProxyCommand command, CancellationToken ct = default)
    {
        var ok = await _proxy.UnloadAsync(ct);
        var status = await _proxy.GetStatusAsync(ct);
        if (!ok)
            return Result.Failure<LlamaSwapStatusDto>(status.Error ?? "Proxy unload failed.");
        return Result.Ok(status);
    }
}
