namespace ProjectBeacon.Application.Tasks;

using Application.Persistence;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Advances the task's single-Actor queue. Planner turns are never kept alive while Actors run:
/// after a completed Actor the dispatcher either starts the next Actor or creates a fresh Planner
/// session when the queue is empty or an explicit checkpoint was requested.
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

    public Task DispatchNextAsync(Guid taskId, CancellationToken ct = default) =>
        DispatchNextAsync(taskId, null, ct);

    public async Task DispatchNextAsync(Guid taskId, Guid? completedSessionId, CancellationToken ct = default)
    {
        if (completedSessionId is null)
            return;

        await using var db = _dbFactory.CreateDbContext();
        var completed = await db.PipelineSessions.AsNoTracking()
            .Where(s => s.Id == completedSessionId && s.TaskId == taskId)
            .Select(s => new { s.Role, s.SubtaskId })
            .FirstOrDefaultAsync(ct);
        if (completed is null)
            return;

        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.PipelineStage is not (TaskPipelineStage.Executing or TaskPipelineStage.ReopenedForRevision))
            return;

        // A completed Planner turn must not wake itself. It only hands the queue to Actors.
        if (completed.Role == PipelineRole.Planner)
        {
            await DispatchActorAsync(taskId, ct);
            await AddPlannerNoOpAttentionIfNeededAsync(taskId, completedSessionId.Value, ct);
            return;
        }

        if (completed.Role != PipelineRole.Actor)
            return;

        var activeActors = await db.PipelineSessions.AsNoTracking()
            .AnyAsync(s => s.TaskId == taskId && s.Role == PipelineRole.Actor
                && (s.Status == SessionStatus.Active || s.Status == SessionStatus.WaitingForUser), ct);
        if (activeActors)
            return;

        // Consume a checkpoint exactly once. The guarded update prevents two completion
        // notifications from both waking the Planner for the same explorer subtask.
        var checkpoint = completed.SubtaskId is { } checkpointSubtaskId
            && await db.Subtasks
                .Where(s => s.Id == checkpointSubtaskId && s.TaskId == taskId && s.WakePlannerAfter)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(s => s.WakePlannerAfter, false)
                    .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), ct) == 1;
        if (checkpoint)
        {
            await WakePlannerAsync(taskId, "checkpoint", ct);
            return;
        }

        var pending = await db.Subtasks.AnyAsync(s => s.TaskId == taskId && s.Status == SubtaskStatus.Pending, ct);
        if (pending)
        {
            await DispatchActorAsync(taskId, ct);
            return;
        }

        var unfinished = await db.Subtasks.AnyAsync(s => s.TaskId == taskId
            && (s.Status == SubtaskStatus.InProgress || s.Status == SubtaskStatus.WaitingForUser), ct);
        if (unfinished)
            return;

        await WakePlannerAsync(taskId, "queue-empty", ct);
    }

    private async Task DispatchActorAsync(Guid taskId, CancellationToken ct)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.PipelineStage is not (TaskPipelineStage.Executing or TaskPipelineStage.ReopenedForRevision))
            return;

        var openActors = await db.PipelineSessions.AsNoTracking()
            .Where(s => s.TaskId == taskId && s.Role == PipelineRole.Actor
                && (s.Status == SessionStatus.Ready || s.Status == SessionStatus.Active
                    || s.Status == SessionStatus.WaitingForUser))
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        if (openActors.Any(s => s.Status is SessionStatus.Active or SessionStatus.WaitingForUser))
            return;

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

        // If no native executor is online, the session stays Ready for the human fallback.
        await new LaunchSessionHandler(_dbFactory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(sessionId)), ct);
    }

    private async Task WakePlannerAsync(Guid taskId, string trigger, CancellationToken ct)
    {
        Guid? plannerSessionId = null;
        await using (var db = _dbFactory.CreateDbContext())
        {
            var strategy = db.Database.CreateExecutionStrategy();
            plannerSessionId = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
                if (task?.PipelineStage != TaskPipelineStage.Executing)
                    return null;

                // Serialize concurrent wake attempts on the task row. The stage remains
                // Executing; this compare-and-update is a lightweight claim, not a new state.
                var claimed = await db.Tasks.Where(t => t.Id == taskId
                        && t.PipelineStage == TaskPipelineStage.Executing)
                    .ExecuteUpdateAsync(update => update.SetProperty(t => t.PipelineStage, TaskPipelineStage.Executing), ct);
                if (claimed == 0)
                    return null;

                var alreadyOpen = await db.PipelineSessions.AnyAsync(s => s.TaskId == taskId
                    && s.Role == PipelineRole.Planner
                    && (s.Status == SessionStatus.Ready || s.Status == SessionStatus.Active
                        || s.Status == SessionStatus.WaitingForUser), ct);
                if (alreadyOpen)
                    return null;

                var subtasks = await db.Subtasks.Where(s => s.TaskId == taskId)
                    .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync(ct);
                var pending = subtasks.Any(s => s.Status == SubtaskStatus.Pending);
                var unfinished = subtasks.Any(s => s.Status is SubtaskStatus.InProgress or SubtaskStatus.WaitingForUser);
                var checkpointWake = string.Equals(trigger, "checkpoint", StringComparison.Ordinal);
                if (unfinished || (!checkpointWake && pending))
                    return null;

                var plannerSessions = await db.PipelineSessions
                    .CountAsync(s => s.TaskId == taskId && s.Role == PipelineRole.Planner, ct);
                var wakeCount = Math.Max(0, plannerSessions - 1);
                var wakeLimit = MaxPlannerWakeups();
                if (wakeCount >= wakeLimit)
                {
                    await RecordPlannerAttentionAsync(db, task, 
                        $"Planner wake-up limit ({wakeLimit}) reached. The pipeline remains in Executing and requires a human decision.", ct);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return null;
                }

                var workspace = await db.TaskWorkspaceSections.AsNoTracking()
                    .Where(section => section.TaskId == taskId)
                    .OrderBy(section => section.Key)
                    .Select(section => new TaskWorkspaceSectionSummaryDto(
                        section.Id, section.Key, section.Title, section.Abstract, section.Kind,
                        section.AuthorType, section.AuthorId, section.AuthorRole, section.AuthorProfileKey,
                        section.AuthorSubtaskId, section.AuthorSessionId, section.Revision,
                        section.CreatedAt, section.UpdatedAt))
                    .Take(100)
                    .ToListAsync(ct);

                SpawnedSession spawned;
                try
                {
                    spawned = await _spawner.SpawnAsync(db, task.ProjectId, PipelineRole.Planner, task.Id, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await RecordPlannerAttentionAsync(db, task,
                        $"Planner could not be started automatically: {ex.Message}. Use Retry planner or Start review manually.", ct);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return null;
                }

                if (spawned.ModelBackendId is null)
                {
                    await RecordPlannerAttentionAsync(db, task,
                        "Planner model is not configured. The pipeline remains in Executing and requires a human decision.", ct);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return null;
                }

                var plannerInstructions = await PipelineSupport.RoleInstructionsAsync(
                    db, task.ProjectId, PipelineRole.Planner, ct);
                var prompt = SessionPrompts.PlannerWake(
                    task, subtasks, workspace, wakeCount + 1, wakeLimit, plannerInstructions);
                var session = PipelineSession.Create(task.Id, task.ProjectId, PipelineRole.Planner,
                    prompt, null, spawned.ModelBackendId, spawned.LaunchSpec);
                db.PipelineSessions.Add(session);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return (Guid?)session.Id;
            });
        }

        if (plannerSessionId is { } sessionId)
            await new LaunchSessionHandler(_dbFactory)
                .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(sessionId)), ct);
    }

    private async Task AddPlannerNoOpAttentionIfNeededAsync(Guid taskId, Guid plannerSessionId, CancellationToken ct)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task?.PipelineStage != TaskPipelineStage.Executing)
            return;

        var session = await db.PipelineSessions.AsNoTracking()
            .Where(s => s.Id == plannerSessionId && s.Role == PipelineRole.Planner)
            .Select(s => new { s.Id })
            .FirstOrDefaultAsync(ct);
        if (session is null)
            return;

        var openActors = await db.PipelineSessions.AnyAsync(s => s.TaskId == taskId
            && s.Role == PipelineRole.Actor
            && (s.Status == SessionStatus.Ready || s.Status == SessionStatus.Active
                || s.Status == SessionStatus.WaitingForUser), ct);
        var pending = await db.Subtasks.AnyAsync(s => s.TaskId == taskId && s.Status == SubtaskStatus.Pending, ct);
        if (openActors || pending)
            return;

        var unfinished = await db.Subtasks.AnyAsync(s => s.TaskId == taskId
            && (s.Status == SubtaskStatus.InProgress || s.Status == SubtaskStatus.WaitingForUser), ct);
        if (!unfinished)
            await RecordAttentionAsync(taskId,
                "Planner finished without creating more subtasks or starting Review. Human input is required; use Retry planner or Start review.", ct);
    }

    private async Task RecordAttentionAsync(Guid taskId, string content, CancellationToken ct)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            return;
        await RecordPlannerAttentionAsync(db, task, content, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task RecordPlannerAttentionAsync(
        IBeaconDb db, TaskItem task, string content, CancellationToken ct)
    {
        const string key = "system/planner-status";
        var existing = await db.TaskWorkspaceSections
            .FirstOrDefaultAsync(section => section.TaskId == task.Id && section.Key == key, ct);
        var now = DateTime.UtcNow;
        if (existing is null)
        {
            db.TaskWorkspaceSections.Add(TaskWorkspaceSection.Create(
                task.Id, task.ProjectId, key, "Planner status",
                "Automatic planner progress and reasons a human decision is needed.",
                "Notes", content, "Server", null, PipelineRole.Planner.ToString()));
            return;
        }

        await db.TaskWorkspaceSections.Where(section => section.Id == existing.Id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(section => section.Content, section => section.Content + "\n\n" + content)
                .SetProperty(section => section.Revision, section => section.Revision + 1)
                .SetProperty(section => section.UpdatedAt, now), ct);
    }

    private static int MaxPlannerWakeups()
    {
        var raw = Environment.GetEnvironmentVariable("BEACON_MAX_PLANNER_WAKEUPS");
        return int.TryParse(raw, out var value) && value >= 0 ? Math.Min(value, 100) : 8;
    }
}