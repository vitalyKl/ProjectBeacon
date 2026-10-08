namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Adds a subtask. Instructions are required, and the task pipeline must already be started.
/// </summary>
public class CreateSubtaskHandler : ICommandHandler<CreateSubtaskCommand, Result<SubtaskDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public CreateSubtaskHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<SubtaskDto>> HandleAsync(CreateSubtaskCommand command, CancellationToken ct = default)
    {
        var instructions = command.Request.Instructions?.Trim();
        if (string.IsNullOrWhiteSpace(instructions))
            return Result.Failure<SubtaskDto>("Instructions are required.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<SubtaskDto>("Task not found.");

        if (task.PipelineStage is not (TaskPipelineStage.Planning or TaskPipelineStage.Executing))
            return Result.Failure<SubtaskDto>(task.PipelineStage is null
                ? "Pipeline is not started."
                : $"Cannot create subtask in stage {task.PipelineStage}.");

        var subtask = Subtask.Create(
            instructions, task.Id, task.ProjectId,
            command.Request.AllowedMcpTools, command.Request.AllowedPaths);
        var phases = await db.TaskPhases.Where(p => p.TaskId == task.Id).OrderBy(p => p.SortOrder).ToListAsync(ct);
        var phase = phases.FirstOrDefault(p => p.Status == TaskPhaseStatus.Active)
            ?? phases.FirstOrDefault(p => p.Key == "do")
            ?? phases.FirstOrDefault();
        if (phase is not null)
            subtask.AssignPhase(phase.Id);
        db.Subtasks.Add(subtask);

        try
        {
            task.EnterExecuting();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<SubtaskDto>(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(PipelineMappers.ToDto(subtask));
    }
}
/// <summary>
/// Opens an actor session for a subtask that is not already finished, and only from an executing stage.
/// </summary>
public class StartActorSessionHandler : ICommandHandler<StartActorSessionCommand, Result<PipelineSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISessionSpawner _spawner;

    public StartActorSessionHandler(IBeaconDbFactory dbFactory, ISessionSpawner spawner)
    {
        _dbFactory = dbFactory;
        _spawner = spawner;
    }

    public async Task<Result<PipelineSessionDto>> HandleAsync(StartActorSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineSessionDto>("Task not found.");

        var subtask = await db.Subtasks
            .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId && s.TaskId == task.Id, ct);
        if (subtask is null)
            return Result.Failure<PipelineSessionDto>("Subtask not found.");
        if (subtask.Status is SubtaskStatus.Done or SubtaskStatus.Failed)
            return Result.Failure<PipelineSessionDto>("Subtask is already finished.");

        if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
        {
            try
            {
                task.EnterExecuting();
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<PipelineSessionDto>(ex.Message);
            }
        }
        if (task.PipelineStage != TaskPipelineStage.Executing)
            return Result.Failure<PipelineSessionDto>($"Cannot start actor session in stage {task.PipelineStage ?? TaskPipelineStage.None}.");

        if (subtask.Status == SubtaskStatus.Pending)
            subtask.Start();

        var spawned = await _spawner.SpawnAsync(db, task.ProjectId, PipelineRole.Actor, task.Id, ct);
        var session = PipelineSession.Create(
            task.Id, task.ProjectId, PipelineRole.Actor,
            SessionPrompts.Actor(subtask), subtask.Id, spawned.ModelBackendId, spawned.LaunchSpec);
        db.PipelineSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Result.Ok(PipelineMappers.ToDto(session));
    }
}
/// <summary>
/// Records a subtask diff and summary. Both are required. A finished subtask fails.
/// </summary>
public class ReportSubtaskResultHandler : ICommandHandler<ReportSubtaskResultCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ReportSubtaskResultHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(ReportSubtaskResultCommand command, CancellationToken ct = default)
    {
        var diffRef = command.Request.DiffRef?.Trim();
        var summary = command.Request.Summary?.Trim();
        if (string.IsNullOrWhiteSpace(diffRef) || string.IsNullOrWhiteSpace(summary))
            return Result.Failure<PipelineStateDto>("DiffRef and Summary are required.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        var subtask = await db.Subtasks
            .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId && s.TaskId == task.Id, ct);
        if (subtask is null)
            return Result.Failure<PipelineStateDto>("Subtask not found.");
        if (subtask.Status is SubtaskStatus.Done or SubtaskStatus.Failed)
            return Result.Failure<PipelineStateDto>("Subtask is already finished.");

        if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
        {
            try
            {
                task.EnterExecuting();
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<PipelineStateDto>(ex.Message);
            }
        }

        if (subtask.Status == SubtaskStatus.Pending)
            subtask.Start();

        try
        {
            subtask.ReportResult(diffRef, summary);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineStateDto>(ex.Message);
        }

        foreach (var session in await PipelineSupport.OpenSessionsAsync(db, task.Id, PipelineRole.Actor, ct))
            if (session.SubtaskId == subtask.Id)
                session.Close();

        await db.SaveChangesAsync(ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}
/// <summary>
/// Fails an in-progress subtask. Reason is required.
/// </summary>
public class FailSubtaskHandler : ICommandHandler<FailSubtaskCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public FailSubtaskHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(FailSubtaskCommand command, CancellationToken ct = default)
    {
        var reason = command.Request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<PipelineStateDto>("Reason is required.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        var subtask = await db.Subtasks
            .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId && s.TaskId == task.Id, ct);
        if (subtask is null)
            return Result.Failure<PipelineStateDto>("Subtask not found.");

        if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
        {
            try
            {
                task.EnterExecuting();
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<PipelineStateDto>(ex.Message);
            }
        }

        subtask.ForceFail(reason);

        foreach (var session in await PipelineSupport.OpenSessionsAsync(db, task.Id, PipelineRole.Actor, ct))
            if (session.SubtaskId == subtask.Id)
                session.Close();

        await db.SaveChangesAsync(ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}


