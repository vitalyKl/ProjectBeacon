namespace ProjectBeacon.Application.Agents;

using Application.Common;
using Application.Devices;
using Domain.Enums;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Reads the current project id from the database tenant filter. Null or empty is unresolved.
/// </summary>
public static class ModelProjectScope
{
    public static bool TryGet(IBeaconDb db, out Guid projectId)
    {
        projectId = Guid.Empty;
        var scope = db.FilterProjectId;
        if (scope is null || scope == Guid.Empty)
            return false;
        projectId = scope.Value;
        return true;
    }
}

internal static class ModelAccount
{
    public const string Unresolved = "Account is not resolved.";

    public static bool TryGet(Guid userId) => userId != Guid.Empty;
}

internal static class ModelBackendMappers
{
    public static LocalModelBackendDto ToDto(LocalModelBackend backend) =>
        new(backend.Id, backend.Name, backend.BackendType, backend.LaunchCommand,
            backend.ContextSize, backend.Ttl, backend.ExtraFlags, backend.UserId, backend.UpdatedAt, backend.Concurrent, backend.Note, backend.OpenCodeModel);

    public static AgentTemplateDto ToDto(AgentTemplate template) =>
        new(template.Id, template.Name, template.Mode, template.SoloBackendId,
            template.PlannerBackendId, template.ActorBackendId, template.ReviewBackendId);

    public static RoleBindingDto ToDto(RoleBinding binding) =>
        new(binding.Id, binding.Role, binding.ModelBackendId, binding.ProjectId);
}
/// <summary>
/// Creates or updates a user-owned model backend. Name is required and at most 200 characters. LlamaCpp requires a launch command. Context size and TTL must be non-negative.
/// </summary>
public sealed class UpsertLocalModelBackendHandler : ICommandHandler<UpsertLocalModelBackendCommand, Result<LocalModelBackendDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public UpsertLocalModelBackendHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<LocalModelBackendDto>> HandleAsync(UpsertLocalModelBackendCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 200)
            return Result.Failure<LocalModelBackendDto>("Name is required (max 200 characters).");
        var openCodeModel = request.OpenCodeModel?.Trim() ?? string.Empty;
        var launch = request.LaunchCommand ?? string.Empty;
        if (request.BackendType == ModelBackendType.LlamaCpp)
        {
            if (string.IsNullOrWhiteSpace(launch) || launch.Length > 1000)
                return Result.Failure<LocalModelBackendDto>("LaunchCommand is required (max 1000 characters).");
        }
        else if (openCodeModel.Length == 0 || openCodeModel.Length > 200 || !openCodeModel.Contains('/'))
        {
            return Result.Failure<LocalModelBackendDto>("OpenCode model id is required, like xai/grok-3.");
        }
        if ((request.Note?.Length ?? 0) > 2000)
            return Result.Failure<LocalModelBackendDto>("Note is too long (max 2000 characters).");
        if (request.ContextSize < 0 || request.Ttl < 0)
            return Result.Failure<LocalModelBackendDto>("ContextSize and Ttl must be non-negative.");

        if (!ModelAccount.TryGet(request.UserId))
            return Result.Failure<LocalModelBackendDto>(ModelAccount.Unresolved);

        await using var db = _dbFactory.CreateDbContext();
        if (request.Id is { } id)
        {
            var existing = await db.LocalModelBackends.FirstOrDefaultAsync(b => b.Id == id && b.UserId == request.UserId, ct);
            if (existing is null)
                return Result.Failure<LocalModelBackendDto>("Model backend not found.");
            existing.Update(name, request.BackendType, launch, request.ContextSize, request.Ttl, request.ExtraFlags, request.Concurrent, request.Note, openCodeModel);
            await DesiredState.BumpUserAsync(db, request.UserId, ct);
            await DesiredState.BumpUserProjectsAsync(db, request.UserId, ct);
            await db.SaveChangesAsync(ct);
            return Result.Ok(ModelBackendMappers.ToDto(existing));
        }

        var backend = LocalModelBackend.Create(name, request.BackendType, launch,
            request.ContextSize, request.Ttl, request.UserId, request.ExtraFlags, request.Concurrent, request.Note, openCodeModel);
        db.LocalModelBackends.Add(backend);
        await DesiredState.BumpUserAsync(db, request.UserId, ct);
        await DesiredState.BumpUserProjectsAsync(db, request.UserId, ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ModelBackendMappers.ToDto(backend));
    }
}
/// <summary>
/// Deletes a backend owned by the user. Fails when a role binding still points at it.
/// </summary>
public sealed class DeleteLocalModelBackendHandler : ICommandHandler<DeleteLocalModelBackendCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public DeleteLocalModelBackendHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DeleteLocalModelBackendCommand command, CancellationToken ct = default)
    {
        if (!ModelAccount.TryGet(command.Request.UserId))
            return Result.Failure(ModelAccount.Unresolved);

        await using var db = _dbFactory.CreateDbContext();
        var backend = await db.LocalModelBackends.FirstOrDefaultAsync(
            b => b.Id == command.Request.Id && b.UserId == command.Request.UserId, ct);
        if (backend is null)
            return Result.Failure("Model backend not found.");

        var boundRoles = await db.RoleBindings.IgnoreQueryFilters()
            .Where(r => r.ModelBackendId == backend.Id)
            .Select(r => r.Role)
            .ToListAsync(ct);
        if (boundRoles.Count > 0)
            return Result.Failure($"Model backend is bound to role(s): {string.Join(", ", boundRoles.Select(r => r.ToString().ToLowerInvariant()))}. Unbind first.");

        db.LocalModelBackends.Remove(backend);
        await DesiredState.BumpUserAsync(db, command.Request.UserId, ct);
        await DesiredState.BumpUserProjectsAsync(db, command.Request.UserId, ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
/// <summary>
/// Binds a pipeline role in the current project to a model backend the project owner can use. Fails when project scope or the backend is missing.
/// </summary>
public sealed class SetRoleBindingHandler : ICommandHandler<SetRoleBindingCommand, Result<RoleBindingDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public SetRoleBindingHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<RoleBindingDto>> HandleAsync(SetRoleBindingCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (!ModelProjectScope.TryGet(db, out var projectId))
            return Result.Failure<RoleBindingDto>("Project scope is not resolved.");

        var backendOwner = await db.LocalModelBackends
            .Where(b => b.Id == command.Request.ModelBackendId)
            .Select(b => (Guid?)b.UserId)
            .FirstOrDefaultAsync(ct);
        if (backendOwner is null)
            return Result.Failure<RoleBindingDto>("Model backend not found.");
        var ownerIsMember = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == backendOwner.Value, ct);
        if (!ownerIsMember)
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

        await DesiredState.BumpProjectAsync(db, projectId, ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ModelBackendMappers.ToDto(binding));
    }
}
/// <summary>
/// Removes the current project's binding for a pipeline role and bumps desired workstation state.
/// </summary>
public sealed class RemoveRoleBindingHandler : ICommandHandler<RemoveRoleBindingCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public RemoveRoleBindingHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(RemoveRoleBindingCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (!ModelProjectScope.TryGet(db, out var projectId))
            return Result.Failure("Project scope is not resolved.");

        var binding = await db.RoleBindings.FirstOrDefaultAsync(r => r.Role == command.Request.Role, ct);
        if (binding is null)
            return Result.Failure("Role binding not found.");

        db.RoleBindings.Remove(binding);
        await DesiredState.BumpProjectAsync(db, projectId, ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
/// <summary>
/// Lists the user's backends, templates, and the project's role bindings.
/// </summary>
public sealed class GetModelRegistryHandler : ICommandHandler<GetModelRegistryCommand, Result<ModelRegistryDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetModelRegistryHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ModelRegistryDto>> HandleAsync(GetModelRegistryCommand command, CancellationToken ct = default)
    {
        if (!ModelAccount.TryGet(command.UserId))
            return Result.Failure<ModelRegistryDto>(ModelAccount.Unresolved);

        await using var db = _dbFactory.CreateDbContext();
        var backends = await db.LocalModelBackends
            .Where(b => b.UserId == command.UserId)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
        var templates = await db.AgentTemplates
            .Where(t => t.UserId == command.UserId)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
        var bindings = ModelProjectScope.TryGet(db, out _)
            ? await db.RoleBindings.OrderBy(r => r.Role).ToListAsync(ct)
            : [];

        return Result.Ok(new ModelRegistryDto(
            command.UserId,
            backends.Select(ModelBackendMappers.ToDto).ToList(),
            bindings.Select(ModelBackendMappers.ToDto).ToList(),
            templates.Select(ModelBackendMappers.ToDto).ToList()));
    }
}
/// <summary>
/// Creates or updates a user-owned agent template. Name is required and at most 200 characters. Referenced backends must exist.
/// </summary>
public sealed class SaveAgentTemplateHandler : ICommandHandler<SaveAgentTemplateCommand, Result<AgentTemplateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public SaveAgentTemplateHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<AgentTemplateDto>> HandleAsync(SaveAgentTemplateCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        var name = request.Name?.Trim() ?? string.Empty;
        if (!ModelAccount.TryGet(request.UserId))
            return Result.Failure<AgentTemplateDto>(ModelAccount.Unresolved);
        if (name.Length == 0 || name.Length > 200)
            return Result.Failure<AgentTemplateDto>("Name is required (max 200 characters).");

        await using var db = _dbFactory.CreateDbContext();
        var ids = new[] { request.SoloBackendId, request.PlannerBackendId, request.ActorBackendId, request.ReviewBackendId }
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (ids.Count > 0)
        {
            var owned = await db.LocalModelBackends.CountAsync(b => ids.Contains(b.Id) && b.UserId == request.UserId, ct);
            if (owned != ids.Count)
                return Result.Failure<AgentTemplateDto>("Model backend not found.");
        }

        if (request.Id is { } id)
        {
            var existing = await db.AgentTemplates.FirstOrDefaultAsync(t => t.Id == id && t.UserId == request.UserId, ct);
            if (existing is null)
                return Result.Failure<AgentTemplateDto>("Template not found.");
            existing.Update(name, request.Mode, request.SoloBackendId, request.PlannerBackendId, request.ActorBackendId, request.ReviewBackendId);
            await db.SaveChangesAsync(ct);
            return Result.Ok(ModelBackendMappers.ToDto(existing));
        }

        var created = AgentTemplate.Create(request.UserId, name, request.Mode, request.SoloBackendId, request.PlannerBackendId, request.ActorBackendId, request.ReviewBackendId);
        db.AgentTemplates.Add(created);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ModelBackendMappers.ToDto(created));
    }
}
/// <summary>
/// Deletes a template owned by the user.
/// </summary>
public sealed class DeleteAgentTemplateHandler : ICommandHandler<DeleteAgentTemplateCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public DeleteAgentTemplateHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DeleteAgentTemplateCommand command, CancellationToken ct = default)
    {
        if (!ModelAccount.TryGet(command.Request.UserId))
            return Result.Failure(ModelAccount.Unresolved);

        await using var db = _dbFactory.CreateDbContext();
        var template = await db.AgentTemplates.FirstOrDefaultAsync(
            t => t.Id == command.Request.Id && t.UserId == command.Request.UserId, ct);
        if (template is null)
            return Result.Failure("Template not found.");
        db.AgentTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
/// <summary>
/// Returns llama-swap status from the workstation proxy.
/// </summary>
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
/// <summary>
/// Asks the workstation proxy to reload llama-swap.
/// </summary>
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
/// <summary>
/// Asks the workstation proxy to unload llama-swap.
/// </summary>
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
