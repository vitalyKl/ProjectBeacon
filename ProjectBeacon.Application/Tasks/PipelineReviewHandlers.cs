namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Application.Common;
using Application.Devices;
using Application.Releases;
using Domain.Entities.Evals;
using Domain.Entities.Tasks;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Opens review from the executing stage, or after a conflicted landing has been integrated
/// back into the task branch. Every subtask must be done or failed.
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
        var started = await ReleaseScopeMutationCoordinator.ExecuteForTaskAsync(
            _dbFactory,
            command.Request.TaskId,
            null,
            async (db, task, token) =>
            {
                // The coordinator supplies a tracked row for ordinary task writers. This
                // handler uses the guarded SQL transition below, so keep its snapshot detached.
                db.Entry(task).State = EntityState.Detached;

                var expectedStage = task.PipelineStage;
                var latestLandingStatus = expectedStage == TaskPipelineStage.Approved
                    ? await db.LandingAttempts
                        .Where(attempt => attempt.ProjectId == task.ProjectId && attempt.TaskId == task.Id)
                        .OrderByDescending(attempt => attempt.CreatedAt)
                        .ThenByDescending(attempt => attempt.Id)
                        .Select(attempt => (LandingAttemptStatus?)attempt.Status)
                        .FirstOrDefaultAsync(token)
                    : null;
                var recoveryAfterLandingConflict = latestLandingStatus == LandingAttemptStatus.Conflicted;

                if (expectedStage == TaskPipelineStage.Approved && !recoveryAfterLandingConflict)
                {
                    if (latestLandingStatus is null)
                        return Result.Failure<PipelineSessionDto>(
                            "Cannot start review in stage Approved.");

                    var latestStatus = latestLandingStatus.Value.ToString();
                    return Result.Failure<PipelineSessionDto>(
                        $"Cannot start review in stage Approved: the latest landing attempt is conflicted only when its current status is Conflicted (current status: {latestStatus}).");
                }

                if (expectedStage != TaskPipelineStage.Executing && !recoveryAfterLandingConflict)
                    return Result.Failure<PipelineSessionDto>(
                        $"Cannot start review in stage {expectedStage ?? TaskPipelineStage.None}.");

                var latestVerdict = await db.ReviewVerdicts
                    .Where(verdict => verdict.ProjectId == task.ProjectId && verdict.TaskId == task.Id)
                    .OrderByDescending(verdict => verdict.CreatedAt)
                    .ThenByDescending(verdict => verdict.Id)
                    .Select(verdict => (ReviewVerdictKind?)verdict.Kind)
                    .FirstOrDefaultAsync(token);
                if (recoveryAfterLandingConflict && latestVerdict != ReviewVerdictKind.Approve)
                    return Result.Failure<PipelineSessionDto>(
                        "A conflicted landing can be re-reviewed only while the latest task verdict remains Approve.");

                var subtasks = await db.Subtasks
                    .Where(subtask => subtask.TaskId == task.Id)
                    .OrderBy(subtask => subtask.CreatedAt).ThenBy(subtask => subtask.Id)
                    .ToListAsync(token);
                if (subtasks.Count == 0)
                    return Result.Failure<PipelineSessionDto>("No subtasks to review.");
                if (subtasks.Any(subtask => subtask.Status is
                    SubtaskStatus.Pending or SubtaskStatus.InProgress or SubtaskStatus.WaitingForUser))
                    return Result.Failure<PipelineSessionDto>(
                        "All subtasks must be done or failed before review.");

                var expectedStatus = task.Status;
                try
                {
                    if (recoveryAfterLandingConflict)
                    {
                        task.ReopenForRevision();
                        task.EnterExecuting();
                    }
                    task.EnterReview();
                }
                catch (InvalidOperationException exception)
                {
                    return Result.Failure<PipelineSessionDto>(exception.Message, ErrorKind.Conflict);
                }

                var transition = await GuardedTransition.TryAsync(
                    db.Tasks.Where(item => item.Id == task.Id),
                    item => item.PipelineStage == expectedStage && item.Status == expectedStatus,
                    item => item.PipelineStage == task.PipelineStage && item.Status == task.Status,
                    update => update
                        .SetProperty(item => item.PipelineStage, task.PipelineStage)
                        .SetProperty(item => item.Status, task.Status),
                    token);
                if (transition != GuardedTransitionResult.Applied)
                    return Result.Failure<PipelineSessionDto>(
                        "Pipeline state changed while starting review; retry.", ErrorKind.Conflict);

                SpawnedSession spawned;
                try
                {
                    spawned = await _spawner.SpawnAsync(
                        db, task.ProjectId, PipelineRole.Review, task.Id, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return Result.Failure<PipelineSessionDto>(exception.Message, ErrorCodes.InternalError);
                }

                if (spawned.ModelBackendId is null)
                    return Result.Failure<PipelineSessionDto>(
                        "No model is configured for the Review role. Configure the Review binding before starting review.",
                        ErrorKind.Conflict);

                var profileInstructions = await PipelineSupport.RoleInstructionsAsync(
                    db, task.ProjectId, PipelineRole.Review, token);
                var session = PipelineSession.Create(
                    task.Id, task.ProjectId, PipelineRole.Review,
                    SessionPrompts.Review(task, subtasks, profileInstructions),
                    null, spawned.ModelBackendId, spawned.LaunchSpec);
                db.PipelineSessions.Add(session);
                await db.SaveChangesAsync(token);
                return Result.Ok(PipelineMappers.ToDto(session));
            },
            ct);

        if (!started.Success || started.Value is null || !command.Request.AutoLaunch)
            return started;

        var launched = await new LaunchSessionHandler(_dbFactory)
            .HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(started.Value.Id)), ct);
        // Keep the Ready session as a manual fallback when the workstation is offline
        // or cannot launch. The caller receives the original session and the UI can retry.
        return launched.Success && launched.Value is not null
            ? Result.Ok(launched.Value)
            : Result.Ok(started.Value);
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

        return await ReleaseScopeMutationCoordinator.ExecuteForTaskAsync(
            _dbFactory,
            command.Request.TaskId,
            null,
            async (db, task, token) =>
            {
                if (task.PipelineStage != TaskPipelineStage.Reviewing)
                    return Result.Failure<PipelineStateDto>(
                        $"Cannot record verdict in stage {task.PipelineStage ?? TaskPipelineStage.None}.");

                // Keep the task detached: the guarded ExecuteUpdate below is the only
                // writer for its state. SaveChanges should persist verdict/subtask/session
                // rows only, not replay a stale tracked TaskItem update.
                db.Entry(task).State = EntityState.Detached;

                var hasReviewSession = await db.PipelineSessions
                    .AnyAsync(session => session.TaskId == task.Id && session.Role == PipelineRole.Review, token);
                if (!hasReviewSession)
                    return Result.Failure<PipelineStateDto>("No review session found for this task.");

                var expectedStatus = task.Status;
                Guid? subtaskId = null;
                if (command.Request.Kind == ReviewVerdictKind.ReopenSubtask)
                {
                    if (command.Request.SubtaskId is null)
                        return Result.Failure<PipelineStateDto>(
                            "SubtaskId is required for a ReopenSubtask verdict.");

                    var subtask = await db.Subtasks.FirstOrDefaultAsync(
                        item => item.Id == command.Request.SubtaskId.Value && item.TaskId == task.Id, token);
                    if (subtask is null)
                        return Result.Failure<PipelineStateDto>("Subtask not found.");

                    var maxCycles = PipelineSupport.MaxReopenCycles();
                    if (subtask.ReopenCount >= maxCycles)
                        subtask.ForceFail($"Reopen limit ({maxCycles}) exceeded: {note}");
                    else
                        subtask.Reopen(note);
                    subtaskId = subtask.Id;
                }

                var verdict = ReviewVerdict.Create(
                    task.Id, task.ProjectId, command.Request.Kind, note, subtaskId);
                db.ReviewVerdicts.Add(verdict);

                if (command.Request.Kind == ReviewVerdictKind.Approve)
                {
                    var reviewSessionId = await db.PipelineSessions
                        .Where(session => session.TaskId == task.Id && session.Role == PipelineRole.Review
                            && (session.Status == SessionStatus.Ready
                                || session.Status == SessionStatus.Active
                                || session.Status == SessionStatus.WaitingForUser))
                        .OrderByDescending(session => session.Id)
                        .Select(session => (Guid?)session.Id)
                        .FirstOrDefaultAsync(token);
                    if (reviewSessionId is null)
                        return Result.Failure<PipelineStateDto>("No review session found for this task.");

                    db.ReviewRuns.Add(
                        ReviewRun.Start(task.ProjectId, task.Id, ReviewerType.Agent, reviewSessionId));
                }

                try
                {
                    task.SetApproved();
                    if (command.Request.Kind == ReviewVerdictKind.ReopenSubtask)
                        task.ReopenForRevision();
                }
                catch (InvalidOperationException exception)
                {
                    return Result.Failure<PipelineStateDto>(exception.Message, ErrorKind.Conflict);
                }

                foreach (var session in await PipelineSupport.OpenSessionsAsync(
                    db, task.Id, PipelineRole.Review, token))
                    session.Close();

                var claimed = await db.Tasks
                    .Where(item => item.Id == task.Id
                        && item.PipelineStage == TaskPipelineStage.Reviewing
                        && item.Status == expectedStatus)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.PipelineStage, task.PipelineStage)
                        .SetProperty(item => item.Status, task.Status), token);
                if (claimed == 0)
                    return Result.Failure<PipelineStateDto>(
                        "Review state changed while recording verdict; retry.", ErrorKind.Conflict);

                await db.SaveChangesAsync(token);
                return Result.Ok(await PipelineSupport.StateAsync(db, task, token));
            },
            ct);
    }
}
/// <summary>
/// Enqueues the review check command for the open review run. Human actors supply the command
/// in the request; API tokens may not and fall back to the project's pinned ReviewCheckCommand.
/// The final command travels in the structured TrustedCheckCommand field, never in the payload.
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
        var bodyCommand = command.Request.CheckCommand?.Trim();

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<PipelineStateDto>("Task not found.");

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == task.ProjectId, ct);
        if (project is null)
            return Result.Failure<PipelineStateDto>("Project not found.");

        string checkCommand;
        var pinnedCommand = project.ReviewCheckCommand?.Trim();
        if (command.Actor.IsHuman)
        {
            if (string.IsNullOrWhiteSpace(bodyCommand))
                return Result.Failure<PipelineStateDto>("Check command is required.", ErrorKind.Validation);
            if (!string.IsNullOrWhiteSpace(pinnedCommand)
                && !string.Equals(bodyCommand, pinnedCommand, StringComparison.Ordinal))
                return Result.Failure<PipelineStateDto>(
                    "The requested check command must exactly match the project's pinned ReviewCheckCommand.",
                    ErrorKind.Conflict);
            // Existing projects may still use an explicitly human-approved review check before
            // a project pin is configured. Such a run can complete the task but cannot become
            // landing proof: RequestLandingHandler also requires the project pin to match.
            checkCommand = bodyCommand;
        }
        else
        {
            if (!string.IsNullOrEmpty(bodyCommand))
                return Result.Failure<PipelineStateDto>(
                    "API tokens may not supply a check command; set the project ReviewCheckCommand instead.",
                    ErrorKind.Validation);
            if (string.IsNullOrWhiteSpace(pinnedCommand))
                return Result.Failure<PipelineStateDto>(
                    "No review check command is configured for this project.", ErrorKind.Conflict);
            checkCommand = pinnedCommand;
        }

        var checkCommandHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(checkCommand))).ToLowerInvariant();
        var reviewRun = await db.ReviewRuns.AsNoTracking()
            .Where(r => r.TaskId == task.Id && r.Status == ReviewRunStatus.Started && r.TargetRunId != null)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (reviewRun is null)
            return Result.Failure<PipelineStateDto>("No open review run to check.");

        // A ReviewCheck reads the same local task branch workspace as the Actor/Review
        // sessions. Never accept a caller-selected workstation that is not the task owner.
        var workstation = await ProjectGitSyncPreflight.ResolveTaskWorkstationAsync(
            db, task, DateTime.UtcNow, ct);
        if (workstation.BlockedReason is not null || workstation.Device is null)
            return Result.Failure<PipelineStateDto>(
                ProjectGitSyncPreflight.WorkstationBlockedMessage(workstation.BlockedReason),
                ErrorKind.Conflict);
        if (command.Request.DeviceId != workstation.Device.Id)
            return Result.Failure<PipelineStateDto>(
                "The review check must run on the workstation pinned to this task. Select the task's pinned workstation and retry.",
                ErrorKind.Conflict);
        if (!await ProjectGitSyncPreflight.TryPinTaskWorkstationAsync(
                db, task.Id, workstation.Device.Id, ct))
            return Result.Failure<PipelineStateDto>(
                "The task was pinned to a different workstation concurrently. Reload the task before running the review check.",
                ErrorKind.Conflict);

        var actorId = command.Request.ActorId.ToString();
        // Claim the run before enqueuing: a second concurrent check loses here and never enqueues.
        var claimed = await db.ReviewRuns
            .Where(r => r.Id == reviewRun.Id && r.Status == ReviewRunStatus.Started && r.ReviewerActorId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ReviewerActorId, actorId)
                .SetProperty(r => r.CheckCommandHash, checkCommandHash), ct);
        if (claimed == 0)
            return Result.Failure<PipelineStateDto>("A review check is already in flight for this run.", ErrorKind.Conflict);

        var payload = new JsonObject
        {
            ["reviewRunId"] = reviewRun.Id.ToString("D"),
            ["taskId"] = task.Id.ToString("D"),
            ["checkCommandHash"] = checkCommandHash
        };
        if (!string.IsNullOrWhiteSpace(command.Request.Path))
            payload["path"] = command.Request.Path;

        var queued = await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            command.Request.DeviceId,
            command.Request.ActorId,
            WorkstationCommandKind.RunReviewCheck,
            payload.ToJsonString(),
            task.ProjectId,
            checkCommand)), ct);
        if (!queued.Success)
        {
            // Give the run back: no check command was delivered, so it must stay claimable.
            await db.ReviewRuns
                .Where(r => r.Id == reviewRun.Id && r.Status == ReviewRunStatus.Started && r.ReviewerActorId == actorId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.ReviewerActorId, (string?)null)
                    .SetProperty(r => r.CheckCommandHash, (string?)null), ct);
            return Result.Failure<PipelineStateDto>(
                queued.Error ?? "Failed to enqueue review check.",
                queued.ErrorCode ?? ErrorCodes.For(queued.Kind),
                queued.Kind);
        }

        return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
    }
}


