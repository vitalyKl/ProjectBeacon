namespace ProjectBeacon.Application.Agents;

using Application.Common;
using Application.Devices;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Project, device, user, run mode, and the backend ids to write into OpenCode and the role bindings.
/// </summary>
public record ApplyAgentConfigRequest(
    Guid ProjectId,
    Guid DeviceId,
    Guid UserId,
    AgentRunMode Mode,
    Guid? SoloBackendId,
    Guid? PlannerBackendId,
    Guid? ActorBackendId,
    Guid? ReviewBackendId);
/// <summary>
/// Command for apply agent config.
/// </summary>
public record ApplyAgentConfigCommand(ApplyAgentConfigRequest Request) : ICommand<Result<WorkstationCommandDto>>;
/// <summary>
/// Binds the selected role models and enqueues an OpenCode apply command on the project device. Fails when the project, runtime, or a backend is missing.
/// </summary>
public sealed class ApplyAgentConfigHandler : ICommandHandler<ApplyAgentConfigCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly SetRoleBindingHandler _setBinding;
    private readonly EnqueueCommandHandler _enqueue;

    public ApplyAgentConfigHandler(
        IBeaconDbFactory dbFactory,
        SetRoleBindingHandler setBinding,
        EnqueueCommandHandler enqueue)
    {
        _dbFactory = dbFactory;
        _setBinding = setBinding;
        _enqueue = enqueue;
    }

    public async Task<Result<WorkstationCommandDto>> HandleAsync(ApplyAgentConfigCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        await using var db = _dbFactory.CreateDbContext();
        var member = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == request.ProjectId && m.UserId == request.UserId, ct);
        if (!member)
            return Result.Failure<WorkstationCommandDto>("Project not found.");

        var resolved = await ProjectRuntimeResolver.ResolveAsync(db, request.ProjectId, request.DeviceId, ct);
        if (!resolved.Success)
            return Result.Failure<WorkstationCommandDto>(resolved.Error ?? CommandSandbox.RuntimeRequired);

        var backends = await db.LocalModelBackends
            .Where(b => b.UserId == request.UserId)
            .OrderBy(b => b.Name)
            .Select(b => ModelBackendMappers.ToDto(b))
            .ToListAsync(ct);

        if (request.Mode == AgentRunMode.Solo)
        {
            var solo = request.SoloBackendId ?? backends.FirstOrDefault()?.Id;
            if (solo is not { } soloId)
                return Result.Failure<WorkstationCommandDto>("A model backend is required.");
            foreach (var role in new[] { PipelineRole.Planner, PipelineRole.Actor, PipelineRole.Review })
            {
                var bound = await _setBinding.HandleAsync(new SetRoleBindingCommand(new SetRoleBindingRequest(role, soloId)), ct);
                if (!bound.Success)
                    return Result.Failure<WorkstationCommandDto>(bound.Error ?? "Failed to bind role.");
            }
        }
        else
        {
            var pairs = new (PipelineRole Role, Guid? Id)[]
            {
                (PipelineRole.Planner, request.PlannerBackendId),
                (PipelineRole.Actor, request.ActorBackendId),
                (PipelineRole.Review, request.ReviewBackendId)
            };
            foreach (var (role, id) in pairs)
            {
                if (id is not { } backendId)
                    continue;
                var bound = await _setBinding.HandleAsync(new SetRoleBindingCommand(new SetRoleBindingRequest(role, backendId)), ct);
                if (!bound.Success)
                    return Result.Failure<WorkstationCommandDto>(bound.Error ?? "Failed to bind role.");
            }
        }

        var payload = OpencodePayload.BuildApply(
            request.Mode,
            backends,
            request.Mode == AgentRunMode.Solo ? request.SoloBackendId ?? backends.FirstOrDefault()?.Id : null,
            request.PlannerBackendId,
            request.ActorBackendId,
            request.ReviewBackendId);

        return await _enqueue.HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(
                request.DeviceId, request.UserId, WorkstationCommandKind.ApplyOpencode, payload, request.ProjectId)),
            ct);
    }
}
