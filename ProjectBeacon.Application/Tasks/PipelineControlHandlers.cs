namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Application.Persistence;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Stops one pipeline session and its workstation command.</summary>
public sealed class CancelPipelineSessionHandler : ICommandHandler<CancelPipelineSessionCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public CancelPipelineSessionHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(CancelPipelineSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var session = await db.PipelineSessions
            .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId && s.TaskId == command.Request.TaskId, ct);
        if (session is null)
            return Result.Failure<PipelineStateDto>("Pipeline session not found.");

        var task = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == session.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (session.Status is SessionStatus.Ready or SessionStatus.Active or SessionStatus.WaitingForUser)
                session.Close();

            if (session.Role == PipelineRole.Actor && session.SubtaskId is { } subtaskId)
            {
                var subtask = await db.Subtasks
                    .FirstOrDefaultAsync(s => s.Id == subtaskId && s.TaskId == task.Id, ct);
                if (subtask is not null && subtask.Status is SubtaskStatus.InProgress or SubtaskStatus.WaitingForUser)
                    subtask.Cancel();
            }

            await db.SaveChangesAsync(ct);
            // Also cancel a still-running command if another workflow closed the session row
            // immediately before this request arrived.
            await PipelineSupport.CancelPipelineCommandsAsync(db, task.ProjectId, [session.Id], ct);
            await transaction.CommitAsync(ct);
        }

        return Result.Ok(await ReloadStateAsync(db, task.Id, ct));
    }

    private static async Task<PipelineStateDto> ReloadStateAsync(IBeaconDb db, Guid taskId, CancellationToken ct)
    {
        var current = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == taskId, ct);
        return await PipelineSupport.StateAsync(db, current, ct);
    }
}

/// <summary>Stops a subtask's Actor work and makes it available for another attempt.</summary>
public sealed class CancelSubtaskHandler : ICommandHandler<CancelSubtaskCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public CancelSubtaskHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(CancelSubtaskCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        var subtask = await db.Subtasks
            .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId && s.TaskId == task.Id, ct);
        if (subtask is null)
            return Result.Failure<PipelineStateDto>("Subtask not found.");
        if (subtask.Status is SubtaskStatus.Done or SubtaskStatus.Failed)
            return Result.Failure<PipelineStateDto>("A completed subtask cannot be cancelled.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var sessions = await db.PipelineSessions
            .Where(s => s.TaskId == task.Id && s.Role == PipelineRole.Actor && s.SubtaskId == subtask.Id)
            .ToListAsync(ct);

        foreach (var session in sessions.Where(s =>
            s.Status is SessionStatus.Ready or SessionStatus.Active or SessionStatus.WaitingForUser))
            session.Close();

        subtask.Cancel();
        await db.SaveChangesAsync(ct);
        await PipelineSupport.CancelPipelineCommandsAsync(db, task.ProjectId, sessions.Select(s => s.Id).ToArray(), ct);
        await transaction.CommitAsync(ct);

        var current = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id, ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, current, ct));
    }
}

/// <summary>Stops all currently running work while preserving the plan for a later restart.</summary>
public sealed class CancelPipelineHandler : ICommandHandler<CancelPipelineCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public CancelPipelineHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public CancelPipelineHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<PipelineStateDto>> HandleAsync(CancelPipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
            if (task is null)
                return Result.Failure<PipelineStateDto>("Task not found.");
            if (task.PipelineStage is null or TaskPipelineStage.None)
                return Result.Failure<PipelineStateDto>("Pipeline is not started.");
            if (task.PipelineStage == TaskPipelineStage.Cancelled)
            {
                await transaction.CommitAsync(ct);
                return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
            }
            if (task.PipelineStage == TaskPipelineStage.Closed)
                return Result.Failure<PipelineStateDto>("A closed pipeline cannot be cancelled. Restart it to begin again.");

            var expectedStage = task.PipelineStage;
            var expectedStatus = task.Status;
            var expectedRevision = task.StateRevision;
            var expectedContentRevision = task.ContentRevision;
            task.CancelPipeline();

            var sessions = await db.PipelineSessions
                .Where(s => s.TaskId == task.Id)
                .ToListAsync(ct);
            foreach (var session in sessions.Where(s =>
                s.Status is SessionStatus.Ready or SessionStatus.Active or SessionStatus.WaitingForUser))
                session.Close();

            var activeSubtasks = await db.Subtasks
                .Where(s => s.TaskId == task.Id
                    && (s.Status == SubtaskStatus.InProgress || s.Status == SubtaskStatus.WaitingForUser))
                .ToListAsync(ct);
            foreach (var subtask in activeSubtasks)
                subtask.Cancel();

            var transition = await GuardedTransition.TryAsync(
                db.Tasks,
                t => t.Id == task.Id
                    && t.PipelineStage == expectedStage
                    && t.Status == expectedStatus
                    && t.StateRevision == expectedRevision
                    && t.ContentRevision == expectedContentRevision,
                t => t.Id == task.Id && t.PipelineStage == TaskPipelineStage.Cancelled,
                update => update
                    .SetProperty(t => t.PipelineStage, task.PipelineStage)
                    .SetProperty(t => t.Status, task.Status)
                    .SetProperty(t => t.SubStage, task.SubStage)
                    .SetProperty(t => t.ReviewNotes, task.ReviewNotes)
                    .SetProperty(t => t.CompletedAt, task.CompletedAt)
                    .SetProperty(t => t.StateRevision, task.StateRevision)
                    .SetProperty(t => t.ContentRevision, task.ContentRevision),
                ct);
            if (transition == GuardedTransitionResult.Lost)
                return Result.Failure<PipelineStateDto>("Pipeline state changed while cancelling; retry.");
            if (transition == GuardedTransitionResult.AlreadyInTarget)
            {
                await transaction.CommitAsync(ct);
                db.ChangeTracker.Clear();
                var alreadyCancelled = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id, ct);
                return Result.Ok(await PipelineSupport.StateAsync(db, alreadyCancelled, ct));
            }

            if (task.ContentRevision != expectedContentRevision)
            {
                var attribution = ContentAttribution.FromActor(command.Actor);
                if (attribution.Type is null)
                    attribution = ("System", "pipeline-cancel");
                _contentWriter.AddVersion(db, task, attribution.Type, attribution.Id);
            }
            await db.SaveChangesAsync(ct);
            await PipelineSupport.CancelPipelineCommandsAsync(db, task.ProjectId, sessions.Select(s => s.Id).ToArray(), ct);
            await PipelineSupport.CancelReviewCheckCommandsAsync(db, task.ProjectId, task.Id, ct);
            await transaction.CommitAsync(ct);

            var current = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id, ct);
            return Result.Ok(await PipelineSupport.StateAsync(db, current, ct));
        });
    }
}

/// <summary>Removes the current pipeline run's output and permits a new Planner run.</summary>
public sealed class RestartPipelineHandler : ICommandHandler<RestartPipelineCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public RestartPipelineHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public RestartPipelineHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<PipelineStateDto>> HandleAsync(RestartPipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
            if (task is null)
                return Result.Failure<PipelineStateDto>("Task not found.");
            if (task.PipelineStage is null or TaskPipelineStage.None)
                return Result.Failure<PipelineStateDto>("Pipeline has not been initialized.");

            var expectedStage = task.PipelineStage;
            var expectedStatus = task.Status;
            var expectedRevision = task.StateRevision;
            var expectedContentRevision = task.ContentRevision;
            task.ResetPipeline();

            var sessions = await db.PipelineSessions
                .Where(s => s.TaskId == task.Id)
                .ToListAsync(ct);
            foreach (var session in sessions.Where(s =>
                s.Status is SessionStatus.Ready or SessionStatus.Active or SessionStatus.WaitingForUser))
                session.Close();

            var subtasks = await db.Subtasks.Where(s => s.TaskId == task.Id).ToListAsync(ct);
            var verdicts = await db.ReviewVerdicts.Where(v => v.TaskId == task.Id).ToListAsync(ct);
            var reviewRuns = await db.ReviewRuns.IgnoreQueryFilters()
                .Where(r => r.ProjectId == task.ProjectId && r.TaskId == task.Id).ToListAsync(ct);
            var phases = await db.TaskPhases.Where(p => p.TaskId == task.Id).ToListAsync(ct);
            foreach (var phase in phases)
                phase.SetStatus(TaskPhaseStatus.Pending);

            var updated = await db.Tasks
                .Where(t => t.Id == task.Id
                    && t.PipelineStage == expectedStage
                    && t.Status == expectedStatus
                    && t.StateRevision == expectedRevision
                    && t.ContentRevision == expectedContentRevision)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(t => t.PipelineStage, task.PipelineStage)
                    .SetProperty(t => t.Status, task.Status)
                    .SetProperty(t => t.SubStage, task.SubStage)
                    .SetProperty(t => t.ReviewNotes, task.ReviewNotes)
                    .SetProperty(t => t.CompletedAt, task.CompletedAt)
                    .SetProperty(t => t.StateRevision, task.StateRevision)
                    .SetProperty(t => t.ContentRevision, task.ContentRevision), ct);
            if (updated == 0)
                return Result.Failure<PipelineStateDto>("Pipeline state changed while restarting; retry.");

            if (task.ContentRevision != expectedContentRevision)
            {
                var attribution = ContentAttribution.FromActor(command.Actor);
                if (attribution.Type is null)
                    attribution = ("System", "pipeline-restart");
                _contentWriter.AddVersion(db, task, attribution.Type, attribution.Id);
            }
            await db.SaveChangesAsync(ct);
            await PipelineSupport.CancelPipelineCommandsAsync(db, task.ProjectId, sessions.Select(s => s.Id).ToArray(), ct);
            await PipelineSupport.CancelReviewCheckCommandsAsync(db, task.ProjectId, task.Id, ct);

            db.Subtasks.RemoveRange(subtasks);
            db.ReviewVerdicts.RemoveRange(verdicts);
            db.ReviewRuns.RemoveRange(reviewRuns);
            await db.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);
            var current = await db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id, ct);
            return Result.Ok(await PipelineSupport.StateAsync(db, current, ct));
        });
    }
}
