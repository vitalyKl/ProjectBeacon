namespace ProjectBeacon.Application.Tasks;

using System.Security.Cryptography;
using System.Text;
using Application.Common;
using Application.Authorization;
using Domain.Common;
using Domain.Entities.Agents;
using Domain.Entities.Tasks;
using System.Text.Json;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Adds a subtask. Instructions are required, and the task pipeline must already be started.
/// </summary>
public class CreateSubtaskHandler : ICommandHandler<CreateSubtaskCommand, Result<SubtaskDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public CreateSubtaskHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<SubtaskDto>> HandleAsync(CreateSubtaskCommand command, CancellationToken ct = default)
    {
        var instructions = command.Request.Instructions?.Trim();
        if (string.IsNullOrWhiteSpace(instructions))
            return Result.Failure<SubtaskDto>("Instructions are required.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await PipelineSupport.FindTaskAsync(db, command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<SubtaskDto>("Task not found.");

        var permission = await PipelineExecutionAuthorization.CanDriveAsync(
            db, _authorization, command.Actor, task, PipelineRole.Planner, requiredSubtaskId: null, ct);
        if (!permission.Success)
            return Result.Failure<SubtaskDto>(permission);

        if (task.PipelineStage is not (TaskPipelineStage.Planning or TaskPipelineStage.Executing))
            return Result.Failure<SubtaskDto>(task.PipelineStage is null
                ? "Pipeline is not started."
                : $"Cannot create subtask in stage {task.PipelineStage}.");

        IReadOnlyList<string>? allowedTools = command.Request.AllowedMcpTools;
        IReadOnlyList<string>? allowedPaths = command.Request.AllowedPaths;
        var optionalTools = command.Request.OptionalMcpTools ?? [];
        var profileKey = command.Request.Profile?.Trim();
        BuiltInAgentProfile? resolvedProfile = null;
        if (!string.IsNullOrWhiteSpace(profileKey))
        {
            var profile = BuiltInAgentProfiles.Get(profileKey);
            if (profile is null)
                return Result.Failure<SubtaskDto>(
                    $"Unknown subtask profile '{profileKey}'. Available profiles: {string.Join(", ", BuiltInAgentProfiles.All.Select(p => p.Key))}.");

            AgentProfile? customProfile = null;
            if (command.Actor.UserId is { } profileOwnerId)
                customProfile = await db.AgentProfiles.FirstOrDefaultAsync(
                    profileRow => profileRow.UserId == profileOwnerId && profileRow.Key == profileKey, ct);
            profile = BuiltInAgentProfiles.ApplyOverride(profile, customProfile)!;
            resolvedProfile = profile;

            var defaults = profile.Tools;
            var optionalGrants = new HashSet<string>(StringComparer.Ordinal);
            foreach (var optionalTool in optionalTools)
            {
                if (!McpToolCatalog.TryResolve(optionalTool, out var optionalDescriptor)
                    || !profile.OptionalTools.Contains(optionalDescriptor!.Name, StringComparer.Ordinal))
                    return Result.Failure<SubtaskDto>(
                        $"Tool '{optionalTool}' is not an optional grant for profile '{profile.Key}'.");
                optionalGrants.Add(optionalDescriptor!.Name);
            }
            if (allowedTools is { Count: > 0 })
            {
                var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var toolName in allowedTools)
                {
                    if (!McpToolCatalog.TryResolve(toolName, out var descriptor)
                        || !defaults.Contains(descriptor!.Name, StringComparer.Ordinal))
                        return Result.Failure<SubtaskDto>(
                            $"Tool '{toolName}' is outside profile '{profile.Key}'. A Planner may narrow, but cannot expand, its tool grants.");
                    requested.Add(descriptor!.Name);
                }
                allowedTools = defaults.Where(name => requested.Contains(name)
                    || profile.RequiredTools.Contains(name, StringComparer.Ordinal))
                    .Concat(optionalGrants).Distinct(StringComparer.Ordinal).ToArray();
            }
            else
                allowedTools = defaults.Concat(optionalGrants).Distinct(StringComparer.Ordinal).ToArray();

            if (allowedPaths?.Any(path => !BuiltInAgentProfiles.IsSafeRelativePath(path)) == true)
                return Result.Failure<SubtaskDto>(
                    "allowedPaths must contain safe repository-relative paths without absolute roots or traversal segments.");

            switch (profile.PathPolicy)
            {
                case AgentProfilePathPolicy.FromPlanner:
                    if (allowedPaths is not { Count: > 0 })
                        return Result.Failure<SubtaskDto>(
                            $"Profile '{profile.Key}' requires explicit allowedPaths from the Planner.");
                    break;
                case AgentProfilePathPolicy.Defaults:
                    if (allowedPaths is not { Count: > 0 })
                        allowedPaths = profile.DefaultPaths;
                    else if (allowedPaths.Any(path => !BuiltInAgentProfiles.IsWithinDefaults(path, profile.DefaultPaths)))
                        return Result.Failure<SubtaskDto>(
                            $"Profile '{profile.Key}' allows only paths beneath: {string.Join(", ", profile.DefaultPaths)}.");
                    break;
                case AgentProfilePathPolicy.None:
                    allowedPaths ??= [];
                    break;
            }
        }
        else if (optionalTools.Count > 0)
        {
            return Result.Failure<SubtaskDto>("optionalMcpTools requires a built-in profile.");
        }

        var subtask = Subtask.Create(
            instructions, task.Id, task.ProjectId,
            allowedTools, allowedPaths, profileKey, command.Request.WakePlannerAfter,
            resolvedProfile is null ? null : JsonSerializer.Serialize(resolvedProfile));
        var phases = await db.TaskPhases.Where(p => p.TaskId == task.Id).OrderBy(p => p.SortOrder).ToListAsync(ct);
        var phase = phases.FirstOrDefault(p => p.Status == TaskPhaseStatus.Active)
            ?? phases.FirstOrDefault(p => string.Equals(p.Key, "do", StringComparison.Ordinal));
        if (phase is not null)
            subtask.AssignPhase(phase.Id);
        db.Subtasks.Add(subtask);

        // Unguarded stage write by design: this is the only Planning→Executing claimant
        // and never touches StateRevision, so it cannot create a second finish-work winner.
        // A concurrent finish_work that commits first may leave the stage active on a Done
        // task; pipeline claims re-check the current status before writing.
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
    private readonly IAuthorizationService _authorization;

    public StartActorSessionHandler(
        IBeaconDbFactory dbFactory,
        ISessionSpawner spawner,
        IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _spawner = spawner;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<PipelineSessionDto>> HandleAsync(StartActorSessionCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
            if (task is null)
                return Result.Failure<PipelineSessionDto>("Task not found.");

            var permission = await PipelineExecutionAuthorization.CanDriveAsync(
                db, _authorization, command.Actor, task,
                PipelineRole.Planner, requiredSubtaskId: null, ct);
            if (!permission.Success)
                return Result.Failure<PipelineSessionDto>(permission);

            var subtask = await db.Subtasks.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId && s.TaskId == task.Id, ct);
            if (subtask is null)
                return Result.Failure<PipelineSessionDto>("Subtask not found.");
            if (subtask.Status is SubtaskStatus.Done or SubtaskStatus.Failed)
                return Result.Failure<PipelineSessionDto>("Subtask is already finished.");

            if (subtask.Status is SubtaskStatus.InProgress or SubtaskStatus.WaitingForUser)
            {
                // A retry after an ambiguous commit returns the existing Actor session.
                // WaitingForUser is also active: never call Start() or create a second session
                // while the first Actor is waiting for a human form response.
                var open = (await PipelineSupport.OpenSessionsAsync(db, task.Id, PipelineRole.Actor, ct))
                    .FirstOrDefault(s => s.SubtaskId == subtask.Id);
                if (open is not null)
                    return Result.Ok(PipelineMappers.ToDto(open));
                var status = subtask.Status == SubtaskStatus.WaitingForUser ? "waiting for user" : "in progress";
                return Result.Failure<PipelineSessionDto>(
                    $"Subtask is {status} without an open Actor session. Fail and reopen it to retry.");
            }

            if (task.PipelineStage is not (TaskPipelineStage.Executing or TaskPipelineStage.ReopenedForRevision))
                return Result.Failure<PipelineSessionDto>($"Cannot start actor session in stage {task.PipelineStage ?? TaskPipelineStage.None}.");

            // Retry the stage claim, subtask claim, and session insert as one unit. The
            // conditional stage update also prevents a stale read from starting work after close.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
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

            var entered = await PipelineSupport.EnterExecutingClaimAsync(db, task.Id, ct);
            if (entered == 0)
                return Result.Failure<PipelineSessionDto>("Pipeline state changed while starting session; retry.");

            // The task-row update above serializes actor starts across callers.
            // Only one Actor session may be queued or running for a task at a time.
            var anotherActorIsOpen = await db.PipelineSessions.AnyAsync(s =>
                s.TaskId == task.Id
                && s.Role == PipelineRole.Actor
                && s.SubtaskId != subtask.Id
                && (s.Status == SessionStatus.Ready
                    || s.Status == SessionStatus.Active
                    || s.Status == SessionStatus.WaitingForUser), ct);
            if (anotherActorIsOpen)
                return Result.Failure<PipelineSessionDto>("Another Actor session is already queued or running for this task.");

            subtask.Start();
            var started = await db.Subtasks
                .Where(s => s.Id == subtask.Id && s.Status == SubtaskStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(s => s.Status, subtask.Status)
                    .SetProperty(s => s.UpdatedAt, subtask.UpdatedAt), ct);
            if (started == 0)
                return Result.Failure<PipelineSessionDto>("Subtask state changed while starting session; retry.");

            SpawnedSession spawned;
            try
            {
                spawned = await _spawner.SpawnAsync(db, task.ProjectId, PipelineRole.Actor, task.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Result.Failure<PipelineSessionDto>(ex.Message);
            }

            var profileInstructions = await PipelineSupport.RoleInstructionsAsync(db, task.ProjectId, PipelineRole.Actor, ct);
            var attachments = subtask.AllowedMcpTools.Contains(McpToolNames.TaskListAttachments, StringComparer.Ordinal)
                && subtask.AllowedMcpTools.Contains(McpToolNames.TaskReadAttachment, StringComparer.Ordinal)
                ? await db.TaskAttachments.AsNoTracking()
                    .Where(attachment => attachment.ProjectId == task.ProjectId && attachment.TaskId == task.Id)
                    .OrderBy(attachment => attachment.FileName)
                    .Select(attachment => new SessionPrompts.TaskAttachmentPromptReference(
                        attachment.Id, attachment.FileName, attachment.ContentType, attachment.Length))
                    .ToListAsync(ct)
                : [];
            var session = PipelineSession.Create(
                task.Id, task.ProjectId, PipelineRole.Actor,
                SessionPrompts.Actor(task, subtask, profileInstructions, attachments),
                subtask.Id, spawned.ModelBackendId, spawned.LaunchSpec);
            db.PipelineSessions.Add(session);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Result.Ok(PipelineMappers.ToDto(session));
        });
    }
}
/// <summary>
/// Records a subtask diff and summary. Both are required. A finished subtask fails.
/// </summary>
public class ReportSubtaskResultHandler : ICommandHandler<ReportSubtaskResultCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ReportSubtaskResultHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<PipelineStateDto>> HandleAsync(ReportSubtaskResultCommand command, CancellationToken ct = default)
    {
        var diffRef = command.Request.DiffRef?.Trim() ?? string.Empty;
        var summary = command.Request.Summary?.Trim();
        if (string.IsNullOrWhiteSpace(summary))
            return Result.Failure<PipelineStateDto>("Summary is required.");
        if (summary.Length > Subtask.MaxSummaryLength)
            return Result.Failure<PipelineStateDto>($"Summary exceeds the {Subtask.MaxSummaryLength}-character limit. Shorten the summary and retry.");

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

            var subtask = await db.Subtasks.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId && s.TaskId == task.Id, ct);
            if (subtask is null)
                return Result.Failure<PipelineStateDto>("Subtask not found.");

            var permission = await PipelineExecutionAuthorization.CanDriveAsync(
                db, _authorization, command.Actor, task, PipelineRole.Actor, subtask.Id, ct);
            if (!permission.Success)
                return Result.Failure<PipelineStateDto>(permission);

            if (subtask.Status is SubtaskStatus.Done or SubtaskStatus.Failed)
                return Result.Failure<PipelineStateDto>("Subtask is already finished.");

            if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
            {
                try { task.EnterExecuting(); }
                catch (InvalidOperationException ex) { return Result.Failure<PipelineStateDto>(ex.Message); }
                var entered = await PipelineSupport.EnterExecutingClaimAsync(db, task.Id, ct);
                if (entered == 0)
                    return Result.Failure<PipelineStateDto>("Pipeline state changed while reporting; retry.");
            }

            var expectedStatus = subtask.Status;
            if (subtask.Status == SubtaskStatus.Pending)
                subtask.Start();
            try { subtask.ReportResult(diffRef, summary); }
            catch (InvalidOperationException ex) { return Result.Failure<PipelineStateDto>(ex.Message); }

            var actorSessions = await PipelineSupport.OpenSessionsAsync(db, task.Id, PipelineRole.Actor, ct);
            var actorSession = actorSessions.FirstOrDefault(session => session.SubtaskId == subtask.Id);
            if (actorSession is not null)
                actorSession.Close();

            var reported = await db.Subtasks
                .Where(s => s.Id == subtask.Id && s.Status == expectedStatus)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(s => s.Status, subtask.Status)
                    .SetProperty(s => s.DiffRef, subtask.DiffRef)
                    .SetProperty(s => s.Summary, subtask.Summary)
                    .SetProperty(s => s.UpdatedAt, subtask.UpdatedAt), ct);
            if (reported == 0)
                return Result.Failure<PipelineStateDto>("Subtask state changed while reporting; retry.");

            var reportAttempt = await db.TaskWorkspaceSections.CountAsync(section =>
                section.TaskId == task.Id && section.AuthorSubtaskId == subtask.Id && section.Kind == "Report", ct) + 1;
            db.TaskWorkspaceSections.Add(TaskWorkspaceReports.Create(
                task.Id, task.ProjectId, subtask.Id, reportAttempt, summary, command.Request.Abstract, actorSession?.Id, subtask.ProfileKey));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
        });
    }
}

/// <summary>Fails an in-progress subtask and publishes the failure report atomically with its status.</summary>
public class FailSubtaskHandler : ICommandHandler<FailSubtaskCommand, Result<PipelineStateDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly PipelineActorQueueDispatcher? _pipelineQueue;
    private readonly IAuthorizationService _authorization;

    public FailSubtaskHandler(
        IBeaconDbFactory dbFactory,
        PipelineActorQueueDispatcher? pipelineQueue = null,
        IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _pipelineQueue = pipelineQueue;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<PipelineStateDto>> HandleAsync(FailSubtaskCommand command, CancellationToken ct = default)
    {
        var reason = command.Request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<PipelineStateDto>("Reason is required.");
        if (reason.Length > Subtask.MaxSummaryLength)
            return Result.Failure<PipelineStateDto>($"Failure reason exceeds the {Subtask.MaxSummaryLength}-character limit. Shorten the reason and retry.");

        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var task = await db.Tasks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
            if (task is null)
                return Result.Failure<PipelineStateDto>("Task not found.");

            var subtask = await db.Subtasks.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == command.Request.SubtaskId && s.TaskId == task.Id, ct);
            if (subtask is null)
                return Result.Failure<PipelineStateDto>("Subtask not found.");

            var permission = await PipelineExecutionAuthorization.CanDriveAsync(
                db, _authorization, command.Actor, task, PipelineRole.Actor, subtask.Id, ct);
            if (!permission.Success)
                return Result.Failure<PipelineStateDto>(permission);

            if (subtask.Status == SubtaskStatus.Done)
                return Result.Failure<PipelineStateDto>("Subtask is already done; reopen it through a review verdict.");
            if (subtask.Status == SubtaskStatus.Failed)
            {
                await PipelineSupport.StopActorSessionsAndCancelCommandsAsync(
                    db, task.ProjectId, task.Id, subtask.Id, ct);
                await transaction.CommitAsync(ct);
                return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
            }

            if (task.PipelineStage == TaskPipelineStage.ReopenedForRevision)
            {
                try { task.EnterExecuting(); }
                catch (InvalidOperationException ex) { return Result.Failure<PipelineStateDto>(ex.Message); }
                var entered = await PipelineSupport.EnterExecutingClaimAsync(db, task.Id, ct);
                if (entered == 0)
                    return Result.Failure<PipelineStateDto>("Pipeline state changed while failing; retry.");
            }

            var expectedStatus = subtask.Status;
            subtask.ForceFail(reason);
            var failed = await db.Subtasks
                .Where(s => s.Id == subtask.Id && s.Status == expectedStatus)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(s => s.Status, subtask.Status)
                    .SetProperty(s => s.Summary, subtask.Summary)
                    .SetProperty(s => s.UpdatedAt, subtask.UpdatedAt), ct);
            if (failed == 0)
                return Result.Failure<PipelineStateDto>("Subtask state changed while failing; retry.");

            var actorSessions = await PipelineSupport.OpenSessionsAsync(db, task.Id, PipelineRole.Actor, ct);
            var actorSession = actorSessions.FirstOrDefault(session => session.SubtaskId == subtask.Id);
            var reportAttempt = await db.TaskWorkspaceSections.CountAsync(section =>
                section.TaskId == task.Id && section.AuthorSubtaskId == subtask.Id && section.Kind == "Report", ct) + 1;
            db.TaskWorkspaceSections.Add(TaskWorkspaceReports.Create(
                task.Id, task.ProjectId, subtask.Id, reportAttempt, reason, command.Request.Abstract, actorSession?.Id));
            await db.SaveChangesAsync(ct);
            await PipelineSupport.StopActorSessionsAndCancelCommandsAsync(
                db, task.ProjectId, task.Id, subtask.Id, ct);
            await transaction.CommitAsync(ct);
            return Result.Ok(await PipelineSupport.StateAsync(db, task, ct));
        });

        if (result.Success && _pipelineQueue is not null)
            await _pipelineQueue.DispatchAfterSubtaskFailureAsync(
                command.Request.TaskId, command.Request.SubtaskId, ct);

        return result;
    }
}
