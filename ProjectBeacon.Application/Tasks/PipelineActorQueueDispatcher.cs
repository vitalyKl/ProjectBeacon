namespace ProjectBeacon.Application.Tasks;

using Application.Persistence;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Advances a task's Actor queue after a Planner or Actor command completes.
/// StartActorSessionHandler and LaunchSessionHandler serialize through a guarded
/// task-row update, so concurrent dispatchers converge on one open Actor session.
/// </summary>
public sealed class PipelineActorQueueDispatcher
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISessionSpawner _spawner;

    public PipelineActorQueueDispatcher(IBeaconDbFactory dbFactory, ISessionSpawner spawner)
    {
        _dbFactory = dbFactory;
        _spawner = spawner;
    }

    public async Task DispatchNextAsync(Guid taskId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.PipelineStage is not (TaskPipelineStage.Executing or TaskPipelineStage.ReopenedForRevision))
            return;

        var openActors = await db.PipelineSessions.AsNoTracking()
            .Where(s => s.TaskId == taskId
                && s.Role == PipelineRole.Actor
                && (s.Status == SessionStatus.Ready
                    || s.Status == SessionStatus.Active
                    || s.Status == SessionStatus.WaitingForUser))
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        if (openActors.Any(s => s.Status is SessionStatus.Active or SessionStatus.WaitingForUser))
            return;

        // Resume an existing Ready session before creating another. This recovers a prior
        // launch that could not enqueue a command because the workstation was unavailable.
        var ready = openActors.FirstOrDefault(s => s.Status == SessionStatus.Ready);
        Guid sessionId;
        if (ready is not null)
        {
            sessionId = ready.Id;
        }
        else
        {
            var nextSubtaskId = await db.Subtasks
                .Where(s => s.TaskId == taskId && s.Status == SubtaskStatus.Pending)
                .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
            if (nextSubtaskId is null)
                return;

            var started = await new StartActorSessionHandler(_dbFactory, _spawner)
                .HandleAsync(new StartActorSessionCommand(
                    new StartActorSessionRequest(taskId, nextSubtaskId.Value)), ct);
            if (!started.Success || started.Value is null)
                return;
            sessionId = started.Value.Id;
        }

        // On missing/offline executor the session stays Ready and can be launched from UI.
        await new LaunchSessionHandler(_dbFactory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(sessionId)), ct);
    }
}