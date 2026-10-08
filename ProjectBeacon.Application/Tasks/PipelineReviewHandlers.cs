namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Application.Common;
using Application.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Opens review only from the executing stage, and only when every subtask is done or failed.
/// </summary>
public class StartReviewHandler : ICommandHandler<StartReviewCommand, Result<PipelineSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISessionSpawner _spawner;

    public StartReviewHandler(IBeaconDbFactory dbFactory, ISessionSpawner spawner)
    {
        _dbFactory = dbFactory;
        _spawner = spawner;
    }

    public async Task<Result<PipelineSessionDto>> HandleAsync(StartReviewCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineSessionDto>("Task not found.");

        if (task.PipelineStage != TaskPipelineStage.Executing)
            return Result.Failure<PipelineSessionDto>($"Cannot start review in stage {task.PipelineStage ?? TaskPipelineStage.None}.");

        var subtasks = await db.Subtasks
            .Where(s => s.TaskId == task.Id)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .ToListAsync(ct);
        if (subtasks.Count == 0)
            return Result.Failure<PipelineSessionDto>("No subtasks to review.");
        if (subtasks.Any(s => s.Status is SubtaskStatus.Pending or SubtaskStatus.InProgress))
            return Result.Failure<PipelineSessionDto>("All subtasks must be done or failed before review.");

        try
        {
            task.EnterReview();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineSessionDto>(ex.Message);
        }

        var spawned = await _spawner.SpawnAsync(db, task.ProjectId, PipelineRole.Review, task.Id, ct);
        var session = PipelineSession.Create(
            task.Id, task.ProjectId, PipelineRole.Review,
            SessionPrompts.Review(task, subtasks), null, spawned.ModelBackendId, spawned.LaunchSpec);
        db.PipelineSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Result.Ok(PipelineMappers.ToDto(session));
    }
}
/// <summary>
/// Records approve or reopen. Reopen requires a subtask id. A verdict is accepted only while the task is in review.
/// </summary>
public class RecordReviewVerdictHandler : ICommandHandler<RecordReviewVerdictCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public RecordReviewVerdictHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineStateDto>> HandleAsync(RecordReviewVerdictCommand command, CancellationToken ct = default)
    {
        var note = command.Request.Note?.Trim();
        if (string.IsNullOrWhiteSpace(note))
            return Result.Failure<PipelineStateDto>("Verdict note is required.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        if (task.PipelineStage != TaskPipelineStage.Reviewing)
            return Result.Failure<PipelineStateDto>($"Cannot record verdict in stage {task.PipelineStage ?? TaskPipelineStage.None}.");

        var hasReviewSession = await db.PipelineSessions
            .AnyAsync(s => s.TaskId == task.Id && s.Role == PipelineRole.Review, ct);
        if (!hasReviewSession)
            return Result.Failure<PipelineStateDto>("No review session found for this task.");

        Guid? subtaskId = null;
        if (command.Request.Kind == ReviewVerdictKind.ReopenSubtask)
        {
            if (command.Request.SubtaskId is null)
                return Result.Failure<PipelineStateDto>("SubtaskId is required for a ReopenSubtask verdict.");

            var subtask = await db.Subtasks
                .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId.Value && s.TaskId == task.Id, ct);
            if (subtask is null)
                return Result.Failure<PipelineStateDto>("Subtask not found.");

            var maxCycles = PipelineSupport.MaxReopenCycles();
            if (subtask.ReopenCount >= maxCycles)
                subtask.ForceFail($"Reopen limit ({maxCycles}) exceeded: {note}");
            else
                subtask.Reopen(note);
            subtaskId = subtask.Id;
        }

        var verdict = ReviewVerdict.Create(task.Id, task.ProjectId, command.Request.Kind, note, subtaskId);
        db.ReviewVerdicts.Add(verdict);

        if (command.Request.Kind == ReviewVerdictKind.Approve)
        {
            var reviewSessionId = await db.PipelineSessions
                .Where(s => s.TaskId == task.Id && s.Role == PipelineRole.Review)
                .OrderByDescending(s => s.LaunchedAt)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
            if (reviewSessionId is null)
                return Result.Failure<PipelineStateDto>("No review session found for this task.");

            db.ReviewRuns.Add(ReviewRun.Start(task.ProjectId, task.Id, ReviewerType.Agent, reviewSessionId));
        }

        try
        {
            task.SetApproved();
            if (command.Request.Kind == ReviewVerdictKind.ReopenSubtask)
                task.ReopenForRevision();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PipelineStateDto>(ex.Message);
        }

        foreach (var session in await PipelineSupport.OpenSessionsAsync(db, task.Id, PipelineRole.Review, ct))
            session.Close();

        await db.SaveChangesAsync(ct);
        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}
/// <summary>
/// Enqueues the review check command for the open review run. The check command is required.
/// </summary>
public class RecordReviewCheckHandler : ICommandHandler<RecordReviewCheckCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;

    public RecordReviewCheckHandler(IBeaconDbFactory dbFactory, EnqueueCommandHandler enqueue)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
    }

    public async Task<Result<PipelineStateDto>> HandleAsync(RecordReviewCheckCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Request.CheckCommand))
            return Result.Failure<PipelineStateDto>("Check command is required.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        var reviewRun = await db.ReviewRuns
            .Where(r => r.TaskId == task.Id && r.Status == ReviewRunStatus.Started && r.TargetRunId != null)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (reviewRun is null)
            return Result.Failure<PipelineStateDto>("No open review run to check.");

        reviewRun.BindReviewer(command.Request.ActorId.ToString());
        await db.SaveChangesAsync(ct);

        var payload = new JsonObject
        {
            ["reviewRunId"] = reviewRun.Id.ToString("D"),
            ["checkCommand"] = command.Request.CheckCommand.Trim()
        };
        if (!string.IsNullOrWhiteSpace(command.Request.Path))
            payload["path"] = command.Request.Path;

        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            command.Request.DeviceId,
            command.Request.ActorId,
            WorkstationCommandKind.RunReviewCheck,
            payload.ToJsonString(),
            task.ProjectId)), ct);
        if (!queued.Success)
            return Result.Failure<PipelineStateDto>(queued.Error ?? "Failed to enqueue review check.");

        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}


