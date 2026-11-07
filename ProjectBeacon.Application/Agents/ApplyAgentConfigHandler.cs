namespace ProjectBeacon.Application.Agents;

using Application.Common;
using Application.Authorization;
using Application.Devices;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Project, device, user, run mode, and the backend ids to bind to pipeline roles.
/// </summary>
public record ApplyAgentConfigRequest(
    Guid ProjectId,
    Guid DeviceId,
    Guid UserId,
    AgentRunMode Mode,
    Guid? SoloBackendId,
    Guid? PlannerBackendId,
    Guid? ActorBackendId,
    Guid? ReviewBackendId,
    string? PlannerInstructions = null,
    string? ActorInstructions = null,
    string? ReviewInstructions = null);
/// <summary>
/// Command for apply agent config.
/// </summary>
public record ApplyAgentConfigCommand(ApplyAgentConfigRequest Request, ActorContext Actor) : ICommand<Result>;
/// <summary>
/// Binds the selected role models for the project. Fails when the project, runtime, or a backend is missing.
/// </summary>
public sealed class ApplyAgentConfigHandler : ICommandHandler<ApplyAgentConfigCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly SetRoleBindingHandler _setBinding;

    public ApplyAgentConfigHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        SetRoleBindingHandler setBinding)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _setBinding = setBinding;
    }

    public async Task<Result> HandleAsync(ApplyAgentConfigCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (command.Actor.UserId != request.UserId || !command.Actor.IsHuman)
            return Result.Forbidden();

        await using var db = _dbFactory.CreateDbContext();
        var access = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Project, AuthAction.Administer, projectId: request.ProjectId, ct: ct);
        if (!access.Success)
            return Result.Forbidden();

        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == request.ProjectId && m.UserId == request.UserId, ct);
        if (!member)
            return Result.Failure("Project not found.");

        var resolved = await ProjectRuntimeResolver.ResolveAsync(db, request.ProjectId, request.DeviceId, ct);
        if (!resolved.Success)
            return Result.Failure(resolved.Error ?? CommandSandbox.RuntimeRequired);

        var backends = await db.LocalModelBackends
            .Where(b => b.UserId == request.UserId)
            .OrderBy(b => b.Name)
            .Select(b => ModelBackendMappers.ToDto(b))
            .ToListAsync(ct);

        if (request.Mode == AgentRunMode.Solo)
        {
            var solo = request.SoloBackendId ?? backends.FirstOrDefault()?.Id;
            if (solo is not { } soloId)
                return Result.Failure("A model backend is required.");
            var profileInstructions = new (PipelineRole Role, string? Instructions)[]
            {
                (PipelineRole.Planner, request.PlannerInstructions),
                (PipelineRole.Actor, request.ActorInstructions),
                (PipelineRole.Review, request.ReviewInstructions)
            };
            foreach (var (role, instructions) in profileInstructions)
            {
                var bound = await _setBinding.HandleAsync(new SetRoleBindingCommand(
                    new SetRoleBindingRequest(role, soloId, instructions ?? string.Empty), command.Actor), ct);
                if (!bound.Success)
                    return Result.Failure(bound.Error ?? "Failed to bind role.");
            }
        }
        else
        {
            var pairs = new (PipelineRole Role, Guid? Id, string? Instructions)[]
            {
                (PipelineRole.Planner, request.PlannerBackendId, request.PlannerInstructions),
                (PipelineRole.Actor, request.ActorBackendId, request.ActorInstructions),
                (PipelineRole.Review, request.ReviewBackendId, request.ReviewInstructions)
            };
            foreach (var (role, id, instructions) in pairs)
            {
                if (id is not { } backendId)
                    continue;
                var bound = await _setBinding.HandleAsync(new SetRoleBindingCommand(new SetRoleBindingRequest(role, backendId, instructions ?? string.Empty), command.Actor), ct);
                if (!bound.Success)
                    return Result.Failure(bound.Error ?? "Failed to bind role.");
            }
        }

        return Result.Ok();
    }
}
