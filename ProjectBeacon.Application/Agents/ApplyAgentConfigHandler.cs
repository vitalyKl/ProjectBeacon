namespace ProjectBeacon.Application.Agents;

using Application.Common;
using Application.Devices;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record ApplyAgentConfigRequest(
    Guid ProjectId,
    Guid DeviceId,
    Guid UserId,
    AgentRunMode Mode,
    Guid? SoloBackendId,
    Guid? PlannerBackendId,
    Guid? ActorBackendId,
    Guid? ReviewBackendId);

public record ApplyAgentConfigCommand(ApplyAgentConfigRequest Request) : ICommand<Result<WorkstationCommandDto>>;

public sealed class ApplyAgentConfigHandler : ICommandHandler<ApplyAgentConfigCommand, Result<WorkstationCommandDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly SetRoleBindingHandler _setBinding;
    private readonly EnqueueCommandHandler _enqueue;

    public ApplyAgentConfigHandler(
        IDbContextFactory<BeaconDbContext> dbFactory,
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

        var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ProjectId == request.ProjectId && r.DeviceId == request.DeviceId, ct);
        if (runtime is null)
            return Result.Failure<WorkstationCommandDto>("Project runtime is not attached to this device.");

        var backends = await db.LocalModelBackends.IgnoreQueryFilters()
            .Where(b => b.ProjectId == request.ProjectId)
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
            runtime.LocalRoot,
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
