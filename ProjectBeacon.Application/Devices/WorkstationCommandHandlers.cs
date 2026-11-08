namespace ProjectBeacon.Application.Devices;

using Application.Agents;
using Application.Auth;
using Application.Common;
using Application.Tasks;
using Application.Security;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
/// <summary>
/// Queues a command for an online device. Project commands need a runtime, and the payload is sandboxed.
/// </summary>
public class EnqueueCommandHandler : ICommandHandler<EnqueueCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly TimeProvider _timeProvider;

    public EnqueueCommandHandler(IBeaconDbFactory dbFactory, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<WorkstationCommandDto>> HandleAsync(EnqueueCommandCommand command, CancellationToken ct = default)
    {
        var descriptor = WorkstationCommandCatalog.Get(command.Request.Kind);
        if (!descriptor.Enabled)
            return Result.Failure<WorkstationCommandDto>("This workstation command has been retired.", ErrorKind.Validation);
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked || device.UserId != command.Request.UserId)
            return Result.Failure<WorkstationCommandDto>("Device not found.");
        if (!device.IsOnline(_timeProvider.GetUtcNow().UtcDateTime))
            return Result.Failure<WorkstationCommandDto>("Device is not connected.", ErrorKind.Conflict);

        if (command.Request.ProjectId is { } projectId)
        {
            var member = await db.ProjectMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.ProjectId == projectId && m.UserId == command.Request.UserId, ct);
            if (!member)
                return Result.Failure<WorkstationCommandDto>("Project not found.");
        }

        string? payloadJson = command.Request.PayloadJson;
        string? localRoot = null;
        if (command.Request.Kind == WorkstationCommandKind.SaveWorkstation)
        {
            if (!DesiredState.TryStampWorkstation(payloadJson, out var stored, out var error))
                return Result.Failure<WorkstationCommandDto>(error ?? "Invalid payload.");
            // Optimistic concurrency: the payload must declare the revision its content was
            // based on. The JSON and the incremented revision commit as one UPDATE guarded by
            // that base revision, so a stale client's content can never be stamped as the
            // latest revision — it gets a conflict and must reload and re-apply its edits.
            var baseRevision = DesiredState.ReadRevision(payloadJson);
            if (baseRevision is null)
                return Result.Failure<WorkstationCommandDto>(
                    "payload must include the revision the content is based on.", ErrorKind.Conflict);
            // One short transaction over this single context: the guarded UPDATE and the
            // command INSERT commit together, so a failed insert rolls back the JSON and
            // the revision with it, and a retry with the same base revision succeeds.
            // The whole transaction sits inside the execution strategy: the production
            // Npgsql options enable retrying, which does not support user-initiated
            // transactions started outside a strategy (the ClaimTaskHandler pattern).
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                // A transient failure rolls the attempt back and re-enters this lambda
                // with the failed attempt's command still tracked; detach it so the
                // retried save cannot insert a second command row.
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var written = await db.DaemonDevices
                    .Where(d => d.Id == device.Id && d.DesiredRevision == baseRevision.Value)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.DesiredWorkstationJson, stored)
                        .SetProperty(d => d.DesiredRevision, baseRevision.Value + 1), ct);
                if (written == 0)
                {
                    // PostgreSQL READ COMMITTED snapshots per statement, so this reread sees
                    // the rival's committed revision for the error message.
                    var current = await db.DaemonDevices.AsNoTracking()
                        .Where(d => d.Id == device.Id)
                        .Select(d => (long?)d.DesiredRevision)
                        .FirstOrDefaultAsync(ct);
                    await tx.RollbackAsync(ct);
                    return Result.Failure<WorkstationCommandDto>(current is null
                        ? "Device not found."
                        : $"Workstation config moved from revision {baseRevision.Value} to {current} while saving; reload and re-apply your changes.",
                        ErrorKind.Conflict);
                }
                var queuedSave = WorkstationCommand.Create(
                    device.Id,
                    command.Request.Kind,
                    DesiredState.WithRevision(stored, baseRevision.Value + 1),
                    command.Request.ProjectId,
                    command.Request.UserId);
                db.WorkstationCommands.Add(queuedSave);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Result.Ok(MapCommand(queuedSave));
            });
        }
        if (CommandSandbox.IsProjectKind(command.Request.Kind))
        {
            if (command.Request.ProjectId is not { } rootedProjectId)
                return Result.Failure<WorkstationCommandDto>(CommandSandbox.RuntimeRequired);
            var resolved = await ProjectRuntimeResolver.ResolveAsync(db, rootedProjectId, device.Id, ct);
            if (!resolved.Success)
                return Result.Failure<WorkstationCommandDto>(resolved.Error ?? CommandSandbox.RuntimeRequired);
            var runtime = resolved.Value!;
            var sanitized = CommandSandbox.SanitizeProjectPayload(payloadJson, command.Request.Kind);
            if (!sanitized.Success)
                return Result.Failure<WorkstationCommandDto>(sanitized.Error ?? "Invalid payload.");
            payloadJson = sanitized.Value;
            localRoot = runtime.LocalRoot;
            if (command.Request.Kind == WorkstationCommandKind.RunReviewCheck)
            {
                if (string.IsNullOrWhiteSpace(command.Request.TrustedCheckCommand))
                    return Result.Failure<WorkstationCommandDto>("A trusted review check command is required.", ErrorKind.Conflict);
                var json = JsonNode.Parse(payloadJson ?? "{}") as JsonObject ?? new JsonObject();
                json["checkCommand"] = command.Request.TrustedCheckCommand.Trim();
                payloadJson = json.ToJsonString();
            }
        }

        var queued = WorkstationCommand.Create(
            device.Id,
            command.Request.Kind,
            payloadJson,
            command.Request.ProjectId,
            command.Request.UserId);
        db.WorkstationCommands.Add(queued);
        await db.SaveChangesAsync(ct);
        return Result.Ok(MapCommand(queued, localRoot));
    }

    internal static WorkstationCommandDto MapCommand(WorkstationCommand command, string? localRoot = null) =>
        new(command.Id, command.DeviceId, command.ProjectId, command.RequestedByUserId, command.Kind, command.Status,
            command.PayloadJson, command.ResultJson, command.Error, command.CreatedAt, command.StartedAt, command.CompletedAt, localRoot,
            command.Version, command.Attempts + 1);
}
/// <summary>
/// Returns the next pending command for the device, or null when the queue is empty.
/// Claiming is a guarded <c>UPDATE ... WHERE Id = @id AND Status = 'Pending'</c>; a concurrent
/// worker that claims the same row first makes the update affect zero rows, so the loser moves
/// on to the next command instead of double-claiming.
/// </summary>
public class ClaimNextCommandHandler : ICommandHandler<ClaimNextCommandCommand, Result<WorkstationCommandDto?>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly TimeProvider _timeProvider;

    public ClaimNextCommandHandler(IBeaconDbFactory dbFactory, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<WorkstationCommandDto?>> HandleAsync(ClaimNextCommandCommand command, CancellationToken ct = default)
    {
        var wait = command.Request.Wait ?? TimeSpan.Zero;
        var deadline = _timeProvider.GetUtcNow().UtcDateTime + wait;
        string? instanceId = null;
        {
            await using var db = _dbFactory.CreateDbContext();
            instanceId = await db.DaemonDevices.AsNoTracking()
                .Where(d => d.Id == command.Request.DeviceId)
                .Select(d => d.ActiveInstanceId)
                .FirstOrDefaultAsync(ct);
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = _dbFactory.CreateDbContext();
            var pending = db.WorkstationCommands.AsNoTracking()
                .Where(c => c.DeviceId == command.Request.DeviceId && c.Status == WorkstationCommandStatus.Pending);
            if (command.Request.ControlOnly)
            {
                // Control commands must be claimable while ChatPrompt occupies the normal lane.
                pending = pending.Where(c =>
                    c.Kind == WorkstationCommandKind.ChatAbort
                    || c.Kind == WorkstationCommandKind.ChatSwitchAgent
                    || c.Kind == WorkstationCommandKind.ChatReplyForm);
            }
            var next = await pending.OrderBy(c => c.CreatedAt).FirstOrDefaultAsync(ct);
            if (next is null)
            {
                if (_timeProvider.GetUtcNow().UtcDateTime >= deadline)
                    return Result.Ok<WorkstationCommandDto?>(null);

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), _timeProvider, ct);
                }
                catch (OperationCanceledException)
                {
                    return Result.Ok<WorkstationCommandDto?>(null);
                }
                continue;
            }

            string? localRoot = null;
            if (CommandSandbox.IsProjectKind(next.Kind))
            {
                localRoot = await ProjectRuntimeResolver.ResolveRootAsync(db, next, ct);
                if (string.IsNullOrWhiteSpace(localRoot))
                {
                    // Guarded fail: only take effect if the row is still pending.
                    var now = _timeProvider.GetUtcNow().UtcDateTime;
                    var updated = await db.WorkstationCommands
                        .Where(c => c.Id == next.Id && c.Status == WorkstationCommandStatus.Pending)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(c => c.Status, WorkstationCommandStatus.Failed)
                            .SetProperty(c => c.Error, CommandSandbox.RuntimeRequired)
                            .SetProperty(c => c.CompletedAt, now)
                            .SetProperty(c => c.StartedAt, now), ct);
                    if (updated == 1)
                        continue;
                    // 0 rows: another worker already advanced this row; retry the queue.
                    continue;
                }
            }

            // Guarded claim: succeeds only if the row is still pending. The claim grants the
            // kind's execution lease and records the claiming process instance.
            var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var lease = CommandLease.For(next.Kind);
            var claimed = await db.WorkstationCommands
                .Where(c => c.Id == next.Id && c.Status == WorkstationCommandStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, WorkstationCommandStatus.Running)
                    .SetProperty(c => c.StartedAt, startedAt)
                    .SetProperty(c => c.LeaseExpiresAt, startedAt + lease)
                    .SetProperty(c => c.ClaimInstanceId, instanceId), ct);
            if (claimed != 1)
                continue; // lost the race; the next pending command (if any) is picked up next iteration.

            return Result.Ok<WorkstationCommandDto?>(new WorkstationCommandDto(
                next.Id, next.DeviceId, next.ProjectId, next.RequestedByUserId, next.Kind,
                WorkstationCommandStatus.Running, next.PayloadJson, next.ResultJson, null,
                next.CreatedAt, startedAt, null, localRoot, next.Version, next.Attempts + 1));
        }
    }
}
/// <summary>
/// Marks a device command succeeded or failed. The finish is a guarded
/// <c>UPDATE ... WHERE Id = @id AND Status = @expected</c>; a concurrent claim, re-queue,
/// or completion that moved the row first makes the update affect zero rows and the loser
/// reports failure without applying the completion effects.
/// </summary>
public class CompleteCommandHandler : ICommandHandler<CompleteCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly PipelineActorQueueDispatcher? _pipelineQueue;

    public CompleteCommandHandler(IBeaconDbFactory dbFactory, PipelineActorQueueDispatcher? pipelineQueue = null)
    {
        _dbFactory = dbFactory;
        _pipelineQueue = pipelineQueue;
    }

    public async Task<Result<WorkstationCommandDto>> HandleAsync(CompleteCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        Guid? dispatchTaskId = null;
        Guid? completedSessionId = null;

        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var row = await db.WorkstationCommands.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == command.Request.CommandId, ct);
            if (row is null || row.DeviceId != command.Request.DeviceId)
                return Result.Failure<WorkstationCommandDto>("Command not found.");
            if (!CommandCompletions.AcceptsGeneration(row, command.Request.Generation))
                return Result.Failure<WorkstationCommandDto>("Command generation mismatch.");

            var expectedStatus = row.Status;
            var error = command.Request.Success
                ? null
                : string.IsNullOrWhiteSpace(command.Request.Error) ? "Command failed." : command.Request.Error;
            try
            {
                if (command.Request.Success)
                    row.Succeed(command.Request.ResultJson);
                else
                    row.Fail(error!);
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<WorkstationCommandDto>(ex.Message);
            }

            var finished = await db.WorkstationCommands
                .Where(c => c.Id == row.Id && c.Status == expectedStatus)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, row.Status)
                    .SetProperty(c => c.ResultJson, row.ResultJson)
                    .SetProperty(c => c.Error, row.Error)
                    .SetProperty(c => c.StartedAt, row.StartedAt)
                    .SetProperty(c => c.CompletedAt, row.CompletedAt)
                    .SetProperty(c => c.LeaseExpiresAt, row.LeaseExpiresAt), ct);
            if (finished == 0)
                return Result.Failure<WorkstationCommandDto>("Command state changed while completing; retry.");

            var harnessTokens = await db.ApiTokens.IgnoreQueryFilters()
                .Where(t => t.ClientKind == ApiTokenClientKind.Harness && t.CommandId == row.Id && t.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var token in harnessTokens)
                token.Revoke();

            if (row.Kind == WorkstationCommandKind.RunPipelineSession)
            {
                completedSessionId = DevicePayloadReader.ReadGuid(row.PayloadJson, "sessionId");
                if (completedSessionId is { } sessionId)
                {
                    dispatchTaskId = await db.PipelineSessions.IgnoreQueryFilters()
                        .Where(s => s.Id == sessionId && s.ProjectId == row.ProjectId)
                        .Select(s => (Guid?)s.TaskId)
                        .FirstOrDefaultAsync(ct);
                }
            }

            try
            {
                await CommandCompletions.ApplyAsync(
                    db, row, command.Request.Success, command.Request.ResultJson, error, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<WorkstationCommandDto>(ex.Message);
            }

            // The command terminal state, revoked harness token, pipeline transition and
            // mandatory workspace failure report are one durable completion.
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            var localRoot = await ProjectRuntimeResolver.ResolveRootAsync(db, row, ct);
            return Result.Ok(EnqueueCommandHandler.MapCommand(row, localRoot));
        });

        if (result.Success && dispatchTaskId is { } taskId && completedSessionId is { } sessionId
            && _pipelineQueue is not null)
            await _pipelineQueue.DispatchNextAsync(taskId, sessionId, ct);

        return result;
    }
}
/// <summary>
/// Loads one command the user owns.
/// </summary>
public class GetCommandHandler : ICommandHandler<GetCommandCommand, Result<WorkstationCommandDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetCommandHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(GetCommandCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var row = await db.WorkstationCommands.FirstOrDefaultAsync(c => c.Id == command.Request.CommandId, ct);
        if (row is null)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == row.DeviceId, ct);
        if (device is null || device.UserId != command.Request.UserId)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        var localRoot = await ProjectRuntimeResolver.ResolveRootAsync(db, row, ct);
        return Result.Ok(EnqueueCommandHandler.MapCommand(row, localRoot));
    }
}
/// <summary>
/// Stores the latest progress snapshot for an active client-update attempt. The guarded update
/// cannot overwrite a terminal result or progress from a newer claim generation.
/// </summary>
public class ReportCommandProgressHandler : ICommandHandler<ReportCommandProgressCommand, Result<WorkstationCommandDto>>
{
    private const int MaxProgressJsonLength = 256 * 1024;
    private readonly IBeaconDbFactory _dbFactory;

    public ReportCommandProgressHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<WorkstationCommandDto>> HandleAsync(
        ReportCommandProgressCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (request.Generation < 1 || string.IsNullOrWhiteSpace(request.ProgressJson)
            || request.ProgressJson.Length > MaxProgressJsonLength)
            return Result.Failure<WorkstationCommandDto>("Invalid command progress payload.");

        bool waitingForUser;
        try
        {
            using var progress = JsonDocument.Parse(request.ProgressJson);
            if (progress.RootElement.ValueKind != JsonValueKind.Object)
                return Result.Failure<WorkstationCommandDto>("Command progress must be a JSON object.");
            waitingForUser = progress.RootElement.TryGetProperty("forms", out var forms)
                && forms.ValueKind == JsonValueKind.Array
                && forms.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return Result.Failure<WorkstationCommandDto>("Command progress must be valid JSON.");
        }

        await using var db = _dbFactory.CreateDbContext();
        var row = await db.WorkstationCommands
            .FirstOrDefaultAsync(c => c.Id == request.CommandId && c.DeviceId == request.DeviceId, ct);
        if (row is null)
            return Result.Failure<WorkstationCommandDto>("Command not found.");
        if (row.Kind is not (WorkstationCommandKind.UpdateClient or WorkstationCommandKind.RunPipelineSession or WorkstationCommandKind.ChatPrompt))
            return Result.Failure<WorkstationCommandDto>("Progress reporting is not supported for this command.", ErrorKind.Conflict);
        if (row.Status != WorkstationCommandStatus.Running || row.Attempts + 1 != request.Generation)
            return Result.Failure<WorkstationCommandDto>("Command is no longer running in this generation.", ErrorKind.Conflict);

        if (row.Kind == WorkstationCommandKind.RunPipelineSession)
            return await ReportPipelineProgressAsync(db, request, waitingForUser, ct);

        if (row.Kind == WorkstationCommandKind.ChatPrompt)
        {
            // Native chat uses this lightweight heartbeat only while an ask_user form is open.
            // The chat turn itself retains its normal bounded timeout once the form is answered.
            var chatLeaseExpiresAt = DateTime.UtcNow
                + (waitingForUser ? TimeSpan.FromDays(7) : CommandLease.For(row.Kind));
            var renewedChatCommand = await db.WorkstationCommands
                .Where(commandRow => commandRow.Id == request.CommandId
                    && commandRow.DeviceId == request.DeviceId
                    && commandRow.Status == WorkstationCommandStatus.Running
                    && commandRow.Attempts + 1 == request.Generation)
                .ExecuteUpdateAsync(update => update.SetProperty(commandRow => commandRow.LeaseExpiresAt, chatLeaseExpiresAt), ct);
            if (renewedChatCommand != 1)
                return Result.Failure<WorkstationCommandDto>("Command state changed while reporting progress.", ErrorKind.Conflict);
            var currentChatCommand = await db.WorkstationCommands.AsNoTracking()
                .FirstAsync(c => c.Id == request.CommandId && c.DeviceId == request.DeviceId, ct);
            return Result.Ok(EnqueueCommandHandler.MapCommand(currentChatCommand));
        }

        var updated = await db.WorkstationCommands
            .Where(c => c.Id == request.CommandId
                && c.DeviceId == request.DeviceId
                && c.Kind == WorkstationCommandKind.UpdateClient
                && c.Status == WorkstationCommandStatus.Running
                && c.Attempts + 1 == request.Generation)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResultJson, request.ProgressJson), ct);
        if (updated != 1)
            return Result.Failure<WorkstationCommandDto>("Command state changed while reporting progress.", ErrorKind.Conflict);

        var current = await db.WorkstationCommands.AsNoTracking()
            .FirstAsync(c => c.Id == request.CommandId && c.DeviceId == request.DeviceId, ct);
        return Result.Ok(EnqueueCommandHandler.MapCommand(current));
    }

    private static async Task<Result<WorkstationCommandDto>> ReportPipelineProgressAsync(
        IBeaconDb db,
        ReportCommandProgressRequest request,
        bool waitingForUser,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // This transaction serializes the Actor subtask -> session -> command progress
            // transitions. FailSubtask uses the same row order, so a late heartbeat cannot
            // restore InProgress/Active after a human or operator has failed the subtask.
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var command = await db.WorkstationCommands
                .FirstOrDefaultAsync(row => row.Id == request.CommandId
                    && row.DeviceId == request.DeviceId, ct);
            if (command is null)
                return Result.Failure<WorkstationCommandDto>("Command not found.");
            if (command.Kind != WorkstationCommandKind.RunPipelineSession
                || command.Status != WorkstationCommandStatus.Running
                || command.Attempts + 1 != request.Generation)
                return Result.Failure<WorkstationCommandDto>(
                    "Command is no longer running in this generation.", ErrorKind.Conflict);

            if (command.ProjectId is not { } projectId)
                return Result.Failure<WorkstationCommandDto>(
                    "Pipeline command has no project scope.", ErrorKind.Conflict);
            var sessionId = DevicePayloadReader.ReadGuid(command.PayloadJson, "sessionId");
            if (sessionId is null)
                return Result.Failure<WorkstationCommandDto>(
                    "Pipeline command has no session id.", ErrorKind.Conflict);

            var session = await db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == sessionId.Value && item.ProjectId == projectId, ct);
            if (session is null)
                return Result.Failure<WorkstationCommandDto>(
                    "Pipeline session not found.", ErrorKind.Conflict);

            if (session.Status == SessionStatus.Closed)
            {
                // subtask_report_result closes an Actor session before the model emits its
                // final assistant message. Let that already-reported success finish normally,
                // but reject progress for any other closed session so the daemon aborts it.
                var actorReportedDone = session.Role == PipelineRole.Actor
                    && session.SubtaskId is { } completedSubtaskId
                    && await db.Subtasks.IgnoreQueryFilters().AnyAsync(
                        subtask => subtask.Id == completedSubtaskId
                            && subtask.TaskId == session.TaskId
                            && subtask.Status == SubtaskStatus.Done,
                        ct);
                if (!actorReportedDone)
                    return Result.Failure<WorkstationCommandDto>(
                        "Pipeline session is closed and no successful Actor result was reported.",
                        ErrorKind.Conflict);

                var completedLease = DateTime.UtcNow + CommandLease.For(command.Kind);
                var renewedCompletedActor = await db.WorkstationCommands
                    .Where(row => row.Id == request.CommandId
                        && row.DeviceId == request.DeviceId
                        && row.Kind == WorkstationCommandKind.RunPipelineSession
                        && row.Status == WorkstationCommandStatus.Running
                        && row.Attempts + 1 == request.Generation)
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.LeaseExpiresAt, completedLease), ct);
                if (renewedCompletedActor != 1)
                    return Result.Failure<WorkstationCommandDto>(
                        "Command state changed while reporting progress.", ErrorKind.Conflict);

                await transaction.CommitAsync(ct);
                var completedActorCommand = await db.WorkstationCommands.AsNoTracking()
                    .FirstAsync(row => row.Id == request.CommandId
                        && row.DeviceId == request.DeviceId, ct);
                return Result.Ok(EnqueueCommandHandler.MapCommand(completedActorCommand));
            }

            if (session.Status is not (SessionStatus.Active or SessionStatus.WaitingForUser))
                return Result.Failure<WorkstationCommandDto>(
                    $"Pipeline session cannot report progress from status {session.Status}.",
                    ErrorKind.Conflict);

            ProjectBeacon.Domain.Common.PipelineActivityV1 incomingActivity;
            try
            {
                incomingActivity = ProjectBeacon.Domain.Common.PipelineActivityV1.Parse(request.ProgressJson);
            }
            catch (JsonException)
            {
                return Result.Failure<WorkstationCommandDto>(
                    "Pipeline activity snapshot must be a valid version 1 JSON object.");
            }

            // Keep answered forms in the durable task transcript even after the runtime removes
            // them from its pending-form registry on the next poll.
            ProjectBeacon.Domain.Common.PipelineActivityV1.TryParse(session.ActivityJson, out var previousActivity);
            var activity = incomingActivity.PreserveAnsweredForms(previousActivity) with
            {
                DeviceId = request.DeviceId.ToString("D"),
                CommandId = request.CommandId.ToString("D")
            };
            var activityJson = activity.ToJson();
            if (string.IsNullOrWhiteSpace(activityJson) || activityJson.Length > 262144)
                return Result.Failure<WorkstationCommandDto>(
                    "Pipeline activity snapshot must be a non-empty JSON payload of at most 256 KiB.");

            var now = DateTime.UtcNow;
            if (session.Role == PipelineRole.Actor)
            {
                if (session.SubtaskId is not { } subtaskId)
                    return Result.Failure<WorkstationCommandDto>(
                        "Actor session has no subtask scope.", ErrorKind.Conflict);

                // Match FailSubtask's write order and guard the transition against terminal
                // states. Exactly one side of a concurrent fail/progress race can win.
                var nextSubtaskStatus = waitingForUser
                    ? SubtaskStatus.WaitingForUser
                    : SubtaskStatus.InProgress;
                var updatedSubtask = await db.Subtasks.IgnoreQueryFilters()
                    .Where(subtask => subtask.Id == subtaskId
                        && subtask.TaskId == session.TaskId
                        && (subtask.Status == SubtaskStatus.InProgress
                            || subtask.Status == SubtaskStatus.WaitingForUser))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(subtask => subtask.Status, nextSubtaskStatus)
                        .SetProperty(subtask => subtask.UpdatedAt, now), ct);
                if (updatedSubtask != 1)
                    return Result.Failure<WorkstationCommandDto>(
                        "Actor subtask is already terminal or no longer active; the workstation turn must stop.",
                        ErrorKind.Conflict);
            }

            var nextSessionStatus = waitingForUser
                ? SessionStatus.WaitingForUser
                : SessionStatus.Active;
            var updatedSession = await db.PipelineSessions.IgnoreQueryFilters()
                .Where(item => item.Id == session.Id
                    && item.ProjectId == projectId
                    && (item.Status == SessionStatus.Active
                        || item.Status == SessionStatus.WaitingForUser))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.ActivityJson, activityJson)
                    .SetProperty(item => item.LastActivityAt, now)
                    .SetProperty(item => item.FailureReason, (string?)null)
                    .SetProperty(item => item.Status, nextSessionStatus), ct);
            if (updatedSession != 1)
                return Result.Failure<WorkstationCommandDto>(
                    "Pipeline session state changed while reporting progress.", ErrorKind.Conflict);

            // Renew only while this generation remains Running. This also prevents a heartbeat
            // racing cancellation/completion from resurrecting an execution lease.
            var lease = waitingForUser ? TimeSpan.FromDays(7) : CommandLease.For(command.Kind);
            var leaseExpiresAt = DateTime.UtcNow + lease;
            var renewed = await db.WorkstationCommands
                .Where(row => row.Id == request.CommandId
                    && row.DeviceId == request.DeviceId
                    && row.Kind == WorkstationCommandKind.RunPipelineSession
                    && row.Status == WorkstationCommandStatus.Running
                    && row.Attempts + 1 == request.Generation)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.LeaseExpiresAt, leaseExpiresAt), ct);
            if (renewed != 1)
                return Result.Failure<WorkstationCommandDto>(
                    "Command state changed while reporting progress.", ErrorKind.Conflict);

            await transaction.CommitAsync(ct);
            var currentPipelineCommand = await db.WorkstationCommands.AsNoTracking()
                .FirstAsync(row => row.Id == request.CommandId
                    && row.DeviceId == request.DeviceId, ct);
            return Result.Ok(EnqueueCommandHandler.MapCommand(currentPipelineCommand));
        });
    }
}

/// <summary>
/// Lists recent commands for a device the user owns.
/// </summary>
public class ListCommandsHandler : ICommandHandler<ListCommandsCommand, Result<IList<WorkstationCommandDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListCommandsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<WorkstationCommandDto>>> HandleAsync(ListCommandsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.FirstOrDefaultAsync(d => d.Id == command.Request.DeviceId, ct);
        if (device is null || device.IsRevoked || device.UserId != command.Request.UserId)
            return Result.Failure<IList<WorkstationCommandDto>>("Device not found.");
        var limit = Math.Clamp(command.Request.Limit, 1, 100);
        var rows = await db.WorkstationCommands
            .Where(c => c.DeviceId == command.Request.DeviceId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
        var projectIds = rows
            .Where(r => r.ProjectId is not null && CommandSandbox.IsProjectKind(r.Kind))
            .Select(r => r.ProjectId!.Value)
            .Distinct()
            .ToList();
        var roots = await ProjectRuntimeResolver.ResolveRootsAsync(db, command.Request.DeviceId, projectIds, ct);
        var mapped = rows.Select(row =>
        {
            string? root = null;
            if (row.ProjectId is { } projectId && CommandSandbox.IsProjectKind(row.Kind))
                roots.TryGetValue(projectId, out root);
            return EnqueueCommandHandler.MapCommand(row, root);
        }).ToList();
        return Result.Ok((IList<WorkstationCommandDto>)mapped);
    }
}
