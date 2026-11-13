namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Common;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Queues a repository operation on the most recently active online project workstation.</summary>
public sealed class EnqueueProjectGitSyncHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditWriter? _auditWriter;

    public EnqueueProjectGitSyncHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        TimeProvider? timeProvider = null,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditWriter = auditWriter;
    }

    public async Task<Result<WorkstationCommandDto>> HandleAsync(
        Guid projectId,
        ActorContext actor,
        GitSyncMode mode,
        CancellationToken ct = default,
        Guid? targetDeviceId = null)
    {
        if (!actor.IsHuman || actor.UserId is not { } userId)
            return Result.Forbidden<WorkstationCommandDto>();
        if (!Enum.IsDefined(mode))
            return Result.Failure<WorkstationCommandDto>("Unsupported GitSync mode.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var permission = await ProjectGitSyncAuthorization.CanAdministerAsync(
                db, actor, _authorization, projectId, ct);
            if (!permission.Success)
                return Result.Failure<WorkstationCommandDto>(
                    permission.Error ?? "Project Owner/Admin permission is required.", ErrorKind.Forbidden);

            // Serialize GitSync enqueue per project across application instances. The no-op
            // revision update acquires a database row lock without changing the business revision.
            var projectLocked = await db.Projects.IgnoreQueryFilters()
                .Where(candidate => candidate.Id == projectId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(candidate => candidate.Revision, candidate => candidate.Revision), ct);
            if (projectLocked == 0)
                return Result.Failure<WorkstationCommandDto>("Project not found.", ErrorKind.NotFound);

            var activeCommands = await db.WorkstationCommands.IgnoreQueryFilters()
                .Where(candidate => candidate.ProjectId == projectId
                    && candidate.Kind == WorkstationCommandKind.GitSync
                    && (candidate.Status == WorkstationCommandStatus.Pending
                        || candidate.Status == WorkstationCommandStatus.Running))
                .OrderByDescending(candidate => candidate.CreatedAt)
                .ThenByDescending(candidate => candidate.Id)
                .ToListAsync(ct);

            var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
                .Where(runtime => runtime.ProjectId == projectId)
                .ToListAsync(ct);
            var deviceIds = runtimes.Select(runtime => runtime.DeviceId).Distinct().ToArray();
            var devices = deviceIds.Length == 0
                ? new List<DaemonDevice>()
                : await db.DaemonDevices.IgnoreQueryFilters()
                    .Where(device => deviceIds.Contains(device.Id) && device.RevokedAt == null)
                    .ToListAsync(ct);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var compatible = runtimes
                .Join(devices, runtime => runtime.DeviceId, device => device.Id,
                    (runtime, device) => new { Runtime = runtime, Device = device })
                .Where(candidate => candidate.Device.IsOnline(now)
                    && WorkstationCapabilities.Supports(candidate.Device.ProbeJson, WorkstationCapabilities.GitSyncV1)
                    && (targetDeviceId is null
                        || ProjectGitSyncPreflight.IsNativeRuntime(candidate.Device.WorkstationJson)))
                .ToList();

            // Match the pipeline's default target when settings initiates a project-wide sync:
            // prefer a native runtime over a newer external-agent runtime. An explicit target
            // from TaskDetail always wins and must be used exactly as requested.
            var selected = targetDeviceId is { } requestedDeviceId
                ? compatible.FirstOrDefault(candidate => candidate.Device.Id == requestedDeviceId)
                : compatible.OrderByDescending(candidate =>
                        ProjectGitSyncPreflight.IsNativeRuntime(candidate.Device.WorkstationJson))
                    .ThenByDescending(candidate => candidate.Device.LastHeartbeatAt)
                    .ThenBy(candidate => candidate.Device.Id)
                    .FirstOrDefault();

            var runningCommands = activeCommands
                .Where(candidate => candidate.Status == WorkstationCommandStatus.Running)
                .ToList();
            var pendingCommands = activeCommands
                .Where(candidate => candidate.Status == WorkstationCommandStatus.Pending)
                .ToList();

            if (runningCommands.Count > 0)
            {
                if (runningCommands.Count != 1
                    || targetDeviceId is not null && targetDeviceId != runningCommands[0].DeviceId
                    || targetDeviceId is null && selected is not null
                        && selected.Device.Id != runningCommands[0].DeviceId)
                    return Result.Failure<WorkstationCommandDto>(
                        "A GitSync command is already running on another workstation for this project.",
                        ErrorKind.Conflict);

                if (!CanSatisfyRequest(runningCommands[0], projectId, mode))
                    return Result.Failure<WorkstationCommandDto>(
                        "A read-only Git status command is already running. Wait for it to finish before requesting synchronization.",
                        ErrorKind.Conflict);

                if (!await CancelPendingCommandsAsync(db, pendingCommands, now, ct))
                    return Result.Failure<WorkstationCommandDto>(
                        "Another workstation claimed a queued GitSync command while duplicate commands were being reconciled. Retry after it finishes.",
                        ErrorKind.Conflict);

                await transaction.CommitAsync(ct);
                return Result.Ok(EnqueueCommandHandler.MapCommand(runningCommands[0]));
            }

            if (selected is null)
                return Result.Failure<WorkstationCommandDto>(
                    targetDeviceId is null
                        ? "No online workstation advertising GitSync support is attached to this project."
                        : "The selected pipeline workstation is offline or does not support GitSync.",
                    ErrorKind.Conflict);

            // Reuse only a queued command for the selected runtime and only when its
            // operation satisfies the requested intent. A Status request may join a queued
            // Pull, but an explicit Pull must replace a queued read-only Status operation.
            var reusablePending = pendingCommands.FirstOrDefault(candidate =>
                candidate.DeviceId == selected.Device.Id
                && CanSatisfyRequest(candidate, projectId, mode));
            if (reusablePending is not null)
            {
                var duplicates = pendingCommands.Where(candidate => candidate.Id != reusablePending.Id).ToList();
                if (!await CancelPendingCommandsAsync(db, duplicates, now, ct))
                    return Result.Failure<WorkstationCommandDto>(
                        "Another workstation claimed a queued GitSync command while duplicate commands were being reconciled. Retry after it finishes.",
                        ErrorKind.Conflict);

                await transaction.CommitAsync(ct);
                return Result.Ok(EnqueueCommandHandler.MapCommand(reusablePending));
            }

            var project = await db.Projects.IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == projectId, ct);
            if (project is null)
                return Result.Failure<WorkstationCommandDto>("Project not found.", ErrorKind.NotFound);

            // Remove all obsolete unclaimed rows atomically. ClaimNextCommandHandler uses
            // the same Pending predicate; if a daemon wins the race, this transaction rolls
            // back and no replacement command is inserted.
            if (!await CancelPendingCommandsAsync(db, pendingCommands, now, ct))
                return Result.Failure<WorkstationCommandDto>(
                    "The queued GitSync command was claimed by its workstation while the target changed. Wait for it to finish before retrying.",
                    ErrorKind.Conflict);

            var payload = JsonSerializer.Serialize(new { projectId, mode = mode.ToString() });
            var command = WorkstationCommand.Create(
                selected.Device.Id,
                WorkstationCommandKind.GitSync,
                payload,
                projectId,
                userId);
            db.WorkstationCommands.Add(command);
            _auditWriter?.Stage(db, AuditEventFactory.ForActor(
                actor,
                "project.git_sync.requested",
                "WorkstationCommand",
                command.Id.ToString(),
                project.OrgId,
                project.Id,
                detailsJson: JsonSerializer.Serialize(new { mode = mode.ToString() })));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Result.Ok(EnqueueCommandHandler.MapCommand(command));
        });
    }

    private static bool CanSatisfyRequest(
        WorkstationCommand command,
        Guid expectedProjectId,
        GitSyncMode requestedMode)
    {
        try
        {
            using var payload = JsonDocument.Parse(command.PayloadJson);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("projectId", out var projectIdElement)
                || projectIdElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(projectIdElement.GetString(), out var payloadProjectId)
                || payloadProjectId != expectedProjectId
                || !root.TryGetProperty("mode", out var modeElement)
                || modeElement.ValueKind != JsonValueKind.String
                || !Enum.TryParse<GitSyncMode>(modeElement.GetString(), ignoreCase: false, out var queuedMode)
                || !Enum.IsDefined(queuedMode))
                return false;

            return queuedMode == requestedMode
                || requestedMode == GitSyncMode.Status && queuedMode == GitSyncMode.PullFastForward;
        }
        catch (JsonException)
        {
            // A malformed pending payload must never be treated as satisfying a new request.
            return false;
        }
    }

    private static async Task<bool> CancelPendingCommandsAsync(
        IBeaconDb db,
        IReadOnlyCollection<WorkstationCommand> pendingCommands,
        DateTime cancelledAt,
        CancellationToken ct)
    {
        foreach (var pending in pendingCommands)
        {
            var updated = await db.WorkstationCommands.IgnoreQueryFilters()
                .Where(candidate => candidate.Id == pending.Id
                    && candidate.Status == WorkstationCommandStatus.Pending)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(candidate => candidate.Status, WorkstationCommandStatus.Cancelled)
                    .SetProperty(candidate => candidate.StartedAt, (DateTime?)null)
                    .SetProperty(candidate => candidate.CompletedAt, cancelledAt)
                    .SetProperty(candidate => candidate.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(candidate => candidate.ClaimInstanceId, (string?)null), ct);
            if (updated != 1)
                return false;
        }

        return true;
    }
}

/// <summary>Reads only a GitSync command belonging to the authorized project.</summary>
public sealed class GetProjectGitSyncCommandHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly TimeProvider _timeProvider;

    public GetProjectGitSyncCommandHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> CanAdministerAsync(
        Guid projectId,
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return false;

        await using var db = _dbFactory.CreateDbContext();
        var permission = await ProjectGitSyncAuthorization.CanAdministerAsync(
            db, actor, _authorization, projectId, ct);
        return permission.Success;
    }

    public async Task<Result<WorkstationCommandDto?>> GetPendingAsync(
        Guid projectId,
        ActorContext actor,
        CancellationToken ct = default,
        Guid? targetDeviceId = null)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<WorkstationCommandDto?>();

        await using var db = _dbFactory.CreateDbContext();
        var permission = await ProjectGitSyncAuthorization.CanAdministerAsync(
            db, actor, _authorization, projectId, ct);
        if (!permission.Success)
            return Result.Failure<WorkstationCommandDto?>(permission);

        var active = await db.WorkstationCommands.IgnoreQueryFilters()
            .Where(candidate => candidate.ProjectId == projectId
                && candidate.Kind == WorkstationCommandKind.GitSync
                && (candidate.Status == WorkstationCommandStatus.Pending
                    || candidate.Status == WorkstationCommandStatus.Running))
            .OrderByDescending(candidate => candidate.CreatedAt)
            .ThenByDescending(candidate => candidate.Id)
            .ToListAsync(ct);

        // A Running command cannot be retargeted. Always restore it, even when the caller
        // is looking at a different pipeline workstation, so the UI can wait for its terminal
        // state instead of accidentally issuing a competing operation.
        var running = active.FirstOrDefault(candidate =>
            candidate.Status == WorkstationCommandStatus.Running);
        if (running is not null)
            return Result.Ok<WorkstationCommandDto?>(EnqueueCommandHandler.MapCommand(running));

        // Pending commands can be restored only when they still have a live, attached daemon
        // capable of claiming them. Without an explicit target, use the same native-first
        // selection as EnqueueProjectGitSyncHandler so project settings tracks the command
        // that a new request would actually reuse.
        var attachedDeviceIds = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(runtime => runtime.ProjectId == projectId)
            .Select(runtime => runtime.DeviceId)
            .Distinct()
            .ToArrayAsync(ct);
        if (attachedDeviceIds.Length == 0)
            return Result.Ok<WorkstationCommandDto?>(null);

        var devices = await db.DaemonDevices.IgnoreQueryFilters()
            .Where(device => attachedDeviceIds.Contains(device.Id))
            .ToListAsync(ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var eligibleDevices = devices
            .Where(device => device.IsOnline(now)
                && WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.GitSyncV1))
            .OrderByDescending(device => ProjectGitSyncPreflight.IsNativeRuntime(device.WorkstationJson))
            .ThenByDescending(device => device.LastHeartbeatAt)
            .ThenBy(device => device.Id)
            .ToList();
        var selectedDeviceId = targetDeviceId ?? eligibleDevices.FirstOrDefault()?.Id;
        if (selectedDeviceId is null
            || !eligibleDevices.Any(device => device.Id == selectedDeviceId.Value))
            return Result.Ok<WorkstationCommandDto?>(null);

        var command = active.FirstOrDefault(candidate =>
            candidate.Status == WorkstationCommandStatus.Pending
            && candidate.DeviceId == selectedDeviceId.Value);
        return Result.Ok<WorkstationCommandDto?>(
            command is null ? null : EnqueueCommandHandler.MapCommand(command));
    }

    public async Task<Result<WorkstationCommandDto>> HandleAsync(
        Guid projectId,
        Guid commandId,
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<WorkstationCommandDto>();

        await using var db = _dbFactory.CreateDbContext();
        var permission = await ProjectGitSyncAuthorization.CanAdministerAsync(
            db, actor, _authorization, projectId, ct);
        if (!permission.Success)
            return Result.Failure<WorkstationCommandDto>(
                permission.Error ?? "Project Owner/Admin permission is required.", ErrorKind.Forbidden);

        var command = await db.WorkstationCommands.IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Id == commandId
                && candidate.ProjectId == projectId
                && candidate.Kind == WorkstationCommandKind.GitSync, ct);
        if (command is null)
            return Result.Failure<WorkstationCommandDto>("GitSync command not found.", ErrorKind.NotFound);

        // Only project/org administrators can inspect this operation. The workstation's local
        // filesystem path is never disclosed through a human-facing endpoint.
        return Result.Ok(EnqueueCommandHandler.MapCommand(command));
    }
}


internal static class ProjectGitSyncAuthorization
{
    public static Task<Result> CanAdministerAsync(
        IBeaconDb db,
        ActorContext actor,
        IAuthorizationService authorization,
        Guid projectId,
        CancellationToken ct)
    {
        // AuthorizationService is the source of truth: besides project Owner/Admin and
        // system admins, it also permits an Owner/Admin of the project's organization.
        return authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
    }
}
