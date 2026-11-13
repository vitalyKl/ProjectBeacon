namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Agents;
using Application.Common;
using Application.Devices;
using Domain.Entities.Tasks;
using Domain.Entities.Devices;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Starts the pipeline and creates the planner session.
/// </summary>
public class StartPipelineHandler : ICommandHandler<StartPipelineCommand, Result<PipelineSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISessionSpawner _spawner;

    public StartPipelineHandler(IBeaconDbFactory dbFactory, ISessionSpawner spawner)
    {
        _dbFactory = dbFactory;
        _spawner = spawner;
    }

    public async Task<Result<PipelineSessionDto>> HandleAsync(StartPipelineCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Resolve the project id, then lock the same project row used by GitSync enqueue
            // and LaunchSession. The final preflight, stage transition, and Planner session
            // creation are committed atomically with respect to repository synchronization.
            var projectId = await db.Tasks.AsNoTracking()
                .Where(task => task.Id == command.Request.TaskId)
                .Select(task => (Guid?)task.ProjectId)
                .FirstOrDefaultAsync(ct);
            if (projectId is null)
                return Result.Failure<PipelineSessionDto>("Task not found.");

            var projectLocked = await db.Projects.IgnoreQueryFilters()
                .Where(project => project.Id == projectId.Value)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(project => project.Revision, project => project.Revision), ct);
            if (projectLocked != 1)
                return Result.Failure<PipelineSessionDto>("Project not found.", ErrorKind.NotFound);

            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == command.Request.TaskId, ct);
            if (task is null)
                return Result.Failure<PipelineSessionDto>("Task not found.");

            // Do not rely on the Web button to enforce these prerequisites: API callers
            // must not be able to move a task into Planning when there is no online native
            // workstation or no usable Planner backend.
            var readiness = await PipelineSupport.StateAsync(db, task, ct);
            if (!readiness.CanStart)
            {
                var message = readiness.StartBlockedReason switch
                {
                    "PipelineNoNativeWorkstation" => "No native workstation is attached to this project.",
                    "PipelineNativeWorkstationOffline" => "The native pipeline workstation is offline.",
                    "PipelineNoPlannerModelConfigured" => "No Planner model is configured for this project.",
                    "PipelinePlannerModelMissing" => "The configured Planner model no longer exists.",
                    "PipelineContextSizeInvalid" => "The configured Planner model has an invalid context size.",
                    _ => "The pipeline prerequisites are not satisfied."
                };
                return Result.Failure<PipelineSessionDto>(message, ErrorKind.Conflict);
            }

            var pipelineDevice = await ProjectGitSyncPreflight.SelectLatestOnlineDeviceAsync(
                db, task.ProjectId, DateTime.UtcNow, ct);
            var repositorySync = await ProjectGitSyncPreflight.EvaluateAsync(
                db, task.ProjectId, pipelineDevice, ct);
            if (repositorySync.InProgress)
                return Result.Failure<PipelineSessionDto>(
                    "GitSync is still pending or running on the selected pipeline workstation. Wait for it to finish before starting the pipeline.",
                    ErrorKind.Conflict);

            var expectedStatus = task.Status;
            try
            {
                task.StartPipeline();
            }
            catch (InvalidOperationException exception)
            {
                return Result.Failure<PipelineSessionDto>(exception.Message);
            }

            // Retain the guarded update as a defense against task-state changes by writers
            // that do not participate in the project-level GitSync lock.
            var transition = await GuardedTransition.TryAsync(
                db.Tasks.Where(candidate => candidate.Id == task.Id),
                candidate => candidate.PipelineStage == null && candidate.Status == expectedStatus,
                candidate => candidate.PipelineStage == task.PipelineStage && candidate.Status == task.Status,
                update => update
                    .SetProperty(candidate => candidate.PipelineStage, candidate => task.PipelineStage)
                    .SetProperty(candidate => candidate.Status, candidate => task.Status),
                ct);
            if (transition != GuardedTransitionResult.Applied)
                return Result.Failure<PipelineSessionDto>(
                    "Pipeline state changed while starting; retry.", ErrorKind.Conflict);

            SpawnedSession spawned;
            try
            {
                spawned = await _spawner.SpawnAsync(
                    db, task.ProjectId, PipelineRole.Planner, task.Id, ct);
                if (spawned.ModelBackendId is null)
                    return Result.Failure<PipelineSessionDto>(
                        "The Planner model could not be resolved. Check the project's Planner model binding.",
                        ErrorKind.Conflict);
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException && ct.IsCancellationRequested)
                    throw;
                return Result.Failure<PipelineSessionDto>(exception.Message);
            }

            var projectName = await PipelineSupport.ProjectNameAsync(db, task.ProjectId, ct);
            var profileInstructions = await PipelineSupport.RoleInstructionsAsync(
                db, task.ProjectId, PipelineRole.Planner, ct);
            var session = PipelineSession.Create(
                task.Id,
                task.ProjectId,
                PipelineRole.Planner,
                SessionPrompts.Planner(task, projectName, profileInstructions),
                null,
                spawned.ModelBackendId,
                spawned.LaunchSpec);
            db.PipelineSessions.Add(session);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Result.Ok(PipelineMappers.ToDto(session));
        });
    }
}
/// <summary>
/// Marks a Ready pipeline session launched.
/// </summary>
public class LaunchSessionHandler : ICommandHandler<LaunchSessionCommand, Result<PipelineSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public LaunchSessionHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<PipelineSessionDto>> HandleAsync(LaunchSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // Reset state before a strategy retry and keep reads, state transition, and command insert
            // in the same transaction. This is required by Npgsql's retrying execution strategy.
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var session = await db.PipelineSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, ct);
            if (session is null)
                return Result.Failure<PipelineSessionDto>("Session not found.");

            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == session.TaskId, ct);
            if (task is null)
                return Result.Failure<PipelineSessionDto>("Task not found.");

            // Serialize the final repository gate and pipeline command insert with GitSync enqueue.
            // Both operations update the same project row inside their transaction, so a new
            // synchronization cannot slip between this check and the launch becoming durable.
            var projectLocked = await db.Projects.IgnoreQueryFilters()
                .Where(project => project.Id == session.ProjectId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(project => project.Revision, project => project.Revision), ct);
            if (projectLocked != 1)
                return Result.Failure<PipelineSessionDto>("Project not found.", ErrorKind.NotFound);

            if (session.Role == PipelineRole.Actor)
            {
                // Serialize Actor launches on the task row. Existing Ready sessions may
                // coexist briefly for recovery, but only one Actor can be running.
                var entered = await PipelineSupport.EnterExecutingClaimAsync(db, task.Id, ct);
                if (entered == 0)
                    return Result.Failure<PipelineSessionDto>("Pipeline state changed while launching; retry.");

                var anotherActorIsRunning = await db.PipelineSessions.AnyAsync(s =>
                    s.TaskId == task.Id
                    && s.Role == PipelineRole.Actor
                    && s.Id != session.Id
                    && (s.Status == SessionStatus.Active || s.Status == SessionStatus.WaitingForUser), ct);
                if (anotherActorIsRunning)
                    return Result.Failure<PipelineSessionDto>("Another Actor session is already running for this task.");
            }
            else if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
            {
                var entered = await PipelineSupport.EnterExecutingClaimAsync(db, task.Id, ct);
                if (entered == 0)
                    return Result.Failure<PipelineSessionDto>("Pipeline state changed while launching; retry.");
            }

            var selectedDevice = await ProjectGitSyncPreflight.SelectLatestOnlineDeviceAsync(
                db, session.ProjectId, DateTime.UtcNow, ct);
            if (selectedDevice is null)
                return Result.Failure<PipelineSessionDto>("The native pipeline workstation is offline.");

            var repositorySync = await ProjectGitSyncPreflight.EvaluateAsync(
                db, session.ProjectId, selectedDevice, ct);
            if (repositorySync.InProgress)
                return Result.Failure<PipelineSessionDto>(
                    "GitSync is still pending or running on the selected pipeline workstation. Wait for it to finish before launching the pipeline session.",
                    ErrorKind.Conflict);

            if (session.ModelBackendId is not { } backendId)
                return Result.Failure<PipelineSessionDto>("No model backend is configured for this pipeline session.");
            var backend = await db.LocalModelBackends
                .FirstOrDefaultAsync(b => b.Id == backendId, ct);
            if (backend is null)
                return Result.Failure<PipelineSessionDto>("The configured pipeline model backend was not found.");
            if (backend.ContextSize <= 0)
                return Result.Failure<PipelineSessionDto>("PipelineContextSizeInvalid");

            var model = ChatModelSelection.ResolveBackendModelId(
                backend.BackendType, backend.Name, backend.ProviderModelId);
            var profileKey = session.Role == PipelineRole.Actor && session.SubtaskId is { } actorSubtaskId
                ? await db.Subtasks.Where(subtask => subtask.Id == actorSubtaskId && subtask.TaskId == task.Id)
                    .Select(subtask => subtask.ProfileKey).FirstOrDefaultAsync(ct)
                : null;
            var profile = BuiltInAgentProfiles.Resolve(profileKey, session.SubtaskId is { } subId
                ? await db.Subtasks.Where(subtask => subtask.Id == subId && subtask.TaskId == task.Id)
                    .Select(subtask => subtask.ProfileSnapshotJson).FirstOrDefaultAsync(ct)
                : null);
            var agentMode = profile?.Mode == AgentProfileMode.Plan ? "plan" : "build";
            var commandsEnabled = profile?.CommandsEnabled ?? true;
            var reasoningEffort = profile?.Effort switch
            {
                "high" => "high",
                "low" or "low-medium" => "low",
                _ => "medium"
            };
            var payload = JsonSerializer.Serialize(new
            {
                projectId = session.ProjectId,
                taskId = session.TaskId,
                subtaskId = session.SubtaskId,
                sessionId = session.Id,
                role = session.Role.ToString(),
                title = $"{session.Role} · {task.Title}",
                prompt = session.PromptContext,
                model,
                contextLimit = backend.ContextSize,
                reasoningEffort,
                agentMode,
                commandsEnabled,
                reportsDiff = profile?.ReportsDiff ?? true
            });
            var pipelineCommand = WorkstationCommand.Create(
                selectedDevice.Id,
                WorkstationCommandKind.RunPipelineSession,
                payload,
                session.ProjectId,
                selectedDevice.UserId);

            try
            {
                session.Launch();
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<PipelineSessionDto>(ex.Message);
            }

            var launched = await db.PipelineSessions
                .Where(s => s.Id == session.Id && s.Status == SessionStatus.Ready)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(s => s.Status, session.Status)
                    .SetProperty(s => s.ExternalSessionId, session.ExternalSessionId)
                    .SetProperty(s => s.LaunchedAt, session.LaunchedAt), ct);
            if (launched == 0)
                return Result.Failure<PipelineSessionDto>("Session state changed while launching; retry.");

            db.WorkstationCommands.Add(pipelineCommand);
            await db.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);
            return Result.Ok(PipelineMappers.ToDto(session));
        });
    }

}
/// <summary>
/// Closes an approved pipeline. Requires review notes and a completed review check.
/// </summary>
public class ApprovePipelineHandler : ICommandHandler<ApprovePipelineCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public ApprovePipelineHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public ApprovePipelineHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<PipelineStateDto>> HandleAsync(ApprovePipelineCommand command, CancellationToken ct = default)
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
            if (task.PipelineStage != TaskPipelineStage.Approved)
                return Result.Failure<PipelineStateDto>("Pipeline is not approved.");

            var notes = command.Request.Note;
            if (string.IsNullOrWhiteSpace(notes))
            {
                notes = await db.ReviewVerdicts
                    .Where(v => v.TaskId == task.Id && v.Kind == ReviewVerdictKind.Approve)
                    .OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id)
                    .Select(v => v.Note)
                    .FirstOrDefaultAsync(ct);
            }
            if (string.IsNullOrWhiteSpace(notes))
                return Result.Failure<PipelineStateDto>("No review notes available to close the pipeline.");
            notes = await PipelineSupport.AppendProposalInboxSummaryAsync(
                db, task.ProjectId, task.Id, notes, ct);

            var runs = await db.ReviewRuns.Where(r => r.TaskId == task.Id).ToListAsync(ct);
            if (!runs.Any(r => r.IsCheckProof()))
                return Result.Failure<PipelineStateDto>("Close requires a completed review check.");

            var expectedStatus = task.Status;
            var expectedContentRevision = task.ContentRevision;
            try
            {
                task.ClosePipeline(notes);
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<PipelineStateDto>(ex.Message);
            }

            foreach (var session in await PipelineSupport.OpenSessionsAsync(db, task.Id, null, ct))
                session.Close();

            var updated = await db.Tasks
                .Where(t => t.Id == task.Id
                    && t.PipelineStage == TaskPipelineStage.Approved
                    && t.Status == expectedStatus
                    && t.ContentRevision == expectedContentRevision)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.PipelineStage, task.PipelineStage)
                    .SetProperty(t => t.Status, task.Status)
                    .SetProperty(t => t.ReviewNotes, task.ReviewNotes)
                    .SetProperty(t => t.CompletedAt, task.CompletedAt)
                    .SetProperty(t => t.ContentRevision, task.ContentRevision), ct);
            if (updated == 0)
                return Result.Failure<PipelineStateDto>("Pipeline state or content changed while closing; retry.");

            if (task.ContentRevision != expectedContentRevision)
            {
                var attribution = ContentAttribution.FromActor(command.Actor);
                if (attribution.Type is null)
                    attribution = Guid.TryParse(command.Request.ActorId, out var userId)
                        ? ("Human", userId.ToString("D"))
                        : ("System", "pipeline-approval");
                _contentWriter.AddVersion(db, task, attribution.Type, attribution.Id);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
        });
    }
}
/// <summary>
/// Closes a pipeline without that check. Fails unless the actor is privileged.
/// </summary>
public class ForceClosePipelineHandler : ICommandHandler<ForceClosePipelineCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public ForceClosePipelineHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public ForceClosePipelineHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<PipelineStateDto>> HandleAsync(ForceClosePipelineCommand command, CancellationToken ct = default)
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

            if (!await PipelineSupport.IsPrivilegedAsync(db, command.Request.ActorId, ct))
                return Result.Failure<PipelineStateDto>("Force close requires admin privileges.");

            var reason = command.Request.Reason?.Trim();
            var marker = string.IsNullOrEmpty(reason)
                ? "force-closed without review"
                : $"force-closed without review: {reason}";
            marker = await PipelineSupport.AppendProposalInboxSummaryAsync(
                db, task.ProjectId, task.Id, marker, ct);

            var expectedStatus = task.Status;
            var expectedStage = task.PipelineStage;
            var expectedContentRevision = task.ContentRevision;
            try
            {
                task.ClosePipeline(marker);
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<PipelineStateDto>(ex.Message);
            }

            foreach (var session in await PipelineSupport.OpenSessionsAsync(db, task.Id, null, ct))
                session.Close();

            var updated = await db.Tasks
                .Where(t => t.Id == task.Id
                    && t.Status == expectedStatus
                    && t.PipelineStage == expectedStage
                    && t.ContentRevision == expectedContentRevision)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.PipelineStage, task.PipelineStage)
                    .SetProperty(t => t.Status, task.Status)
                    .SetProperty(t => t.ReviewNotes, task.ReviewNotes)
                    .SetProperty(t => t.CompletedAt, task.CompletedAt)
                    .SetProperty(t => t.ContentRevision, task.ContentRevision), ct);
            if (updated == 0)
                return Result.Failure<PipelineStateDto>("Pipeline state or content changed while force-closing; retry.");

            if (task.ContentRevision != expectedContentRevision)
            {
                var attribution = ContentAttribution.FromActor(command.Actor);
                if (attribution.Type is null)
                    attribution = Guid.TryParse(command.Request.ActorId, out var userId)
                        ? ("Human", userId.ToString("D"))
                        : ("System", "pipeline-force-close");
                _contentWriter.AddVersion(db, task, attribution.Type, attribution.Id);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
        });
    }
}
/// <summary>
/// Creates a fresh session from a failed or closed pipeline attempt while preserving the original attempt.
/// </summary>
public sealed class RetryPipelineSessionHandler : ICommandHandler<RetryPipelineSessionCommand, Result<PipelineSessionDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISessionSpawner _spawner;

    public RetryPipelineSessionHandler(IBeaconDbFactory dbFactory, ISessionSpawner spawner)
    {
        _dbFactory = dbFactory;
        _spawner = spawner;
    }

    public async Task<Result<PipelineSessionDto>> HandleAsync(RetryPipelineSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var previous = await db.PipelineSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, ct);
            if (previous is null)
                return Result.Failure<PipelineSessionDto>("Session not found.");
            if (previous.Status is not (SessionStatus.Failed or SessionStatus.Closed))
                return Result.Failure<PipelineSessionDto>("Only failed or closed sessions can be retried.");

            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == previous.TaskId, ct);
            if (task is null)
                return Result.Failure<PipelineSessionDto>("Task not found.");

            var expectedStage = previous.Role switch
            {
                PipelineRole.Planner => task.PipelineStage is TaskPipelineStage.Planning or TaskPipelineStage.Executing,
                PipelineRole.Actor => task.PipelineStage is TaskPipelineStage.Executing or TaskPipelineStage.ReopenedForRevision,
                PipelineRole.Review => task.PipelineStage == TaskPipelineStage.Reviewing,
                _ => false
            };
            if (!expectedStage)
                return Result.Failure<PipelineSessionDto>($"Cannot retry {previous.Role} session while the pipeline is in stage {task.PipelineStage}.");

            if (previous.Role == PipelineRole.Actor)
            {
                // Manual retries must obey the same single-Actor slot as automatic dispatch.
                var entered = await PipelineSupport.EnterExecutingClaimAsync(db, task.Id, ct);
                if (entered == 0)
                    return Result.Failure<PipelineSessionDto>("Pipeline state changed while retrying Actor; retry.");

                var anotherActorIsOpen = await db.PipelineSessions.AnyAsync(s =>
                    s.TaskId == task.Id
                    && s.Role == PipelineRole.Actor
                    && s.Id != previous.Id
                    && (s.Status == SessionStatus.Ready
                        || s.Status == SessionStatus.Active
                        || s.Status == SessionStatus.WaitingForUser), ct);
                if (anotherActorIsOpen)
                    return Result.Failure<PipelineSessionDto>("Cannot retry this Actor while another Actor session is queued or running.");
            }

            if (previous.SubtaskId is { } subtaskId)
            {
                var originalSubtask = await db.Subtasks
                    .FirstOrDefaultAsync(s => s.Id == subtaskId && s.TaskId == task.Id, ct);
                if (originalSubtask is null)
                    return Result.Failure<PipelineSessionDto>("The original subtask no longer exists.");
                if (originalSubtask.Status == SubtaskStatus.Done)
                    return Result.Failure<PipelineSessionDto>("A completed subtask cannot be retried.");
                if (previous.Role == PipelineRole.Actor)
                    originalSubtask.PrepareRetry();
            }

            var hasOpenAttempt = await db.PipelineSessions.AnyAsync(s =>
                s.TaskId == previous.TaskId
                && s.SubtaskId == previous.SubtaskId
                && s.Role == previous.Role
                && (s.Status == SessionStatus.Ready || s.Status == SessionStatus.Active || s.Status == SessionStatus.WaitingForUser), ct);
            if (hasOpenAttempt)
                return Result.Failure<PipelineSessionDto>("An open session already exists for this pipeline role.");

            SpawnedSession spawned;
            try
            {
                spawned = await _spawner.SpawnAsync(db, previous.ProjectId, previous.Role, previous.TaskId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Result.Failure<PipelineSessionDto>(ex.Message);
            }

            if (spawned.ModelBackendId is null)
                return Result.Failure<PipelineSessionDto>("No model is configured for this pipeline role. Configure the matching phase or role binding before retrying.");

            var retry = PipelineSession.Create(
                previous.TaskId,
                previous.ProjectId,
                previous.Role,
                previous.PromptContext,
                previous.SubtaskId,
                spawned.ModelBackendId,
                spawned.LaunchSpec);
            db.PipelineSessions.Add(retry);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Ok(PipelineMappers.ToDto(retry));
        });
    }
}
