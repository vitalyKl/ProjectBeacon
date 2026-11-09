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
        CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is not { } userId)
            return Result.Forbidden<WorkstationCommandDto>();
        if (!Enum.IsDefined(mode))
            return Result.Failure<WorkstationCommandDto>("Unsupported GitSync mode.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<WorkstationCommandDto>(
                permission.Error ?? "Project administration permission is required.", ErrorKind.Forbidden);

        var runtimes = await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(runtime => runtime.ProjectId == projectId)
            .ToListAsync(ct);
        if (runtimes.Count == 0)
            return Result.Failure<WorkstationCommandDto>(CommandSandbox.RuntimeRequired, ErrorKind.Conflict);

        var deviceIds = runtimes.Select(runtime => runtime.DeviceId).Distinct().ToArray();
        var devices = await db.DaemonDevices.IgnoreQueryFilters()
            .Where(device => deviceIds.Contains(device.Id) && !device.IsRevoked)
            .ToListAsync(ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var selected = runtimes
            .Join(devices, runtime => runtime.DeviceId, device => device.Id,
                (runtime, device) => new { Runtime = runtime, Device = device })
            .Where(candidate => candidate.Device.IsOnline(now)
                && WorkstationCapabilities.Supports(candidate.Device.ProbeJson, WorkstationCapabilities.GitSyncV1))
            .OrderByDescending(candidate => candidate.Device.LastHeartbeatAt)
            .FirstOrDefault();

        if (selected is null)
            return Result.Failure<WorkstationCommandDto>(
                "No online workstation advertising GitSync support is attached to this project.", ErrorKind.Conflict);

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Id == projectId, ct);
        if (project is null)
            return Result.Failure<WorkstationCommandDto>("Project not found.", ErrorKind.NotFound);

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
            detailsJson: """{"source":"project-settings"}"""));
        await db.SaveChangesAsync(ct);

        return Result.Ok(EnqueueCommandHandler.MapCommand(command));
    }
}

/// <summary>Reads only a GitSync command belonging to the authorized project.</summary>
public sealed class GetProjectGitSyncCommandHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public GetProjectGitSyncCommandHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<WorkstationCommandDto>> HandleAsync(
        Guid projectId,
        Guid commandId,
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is not { } userId)
            return Result.Forbidden<WorkstationCommandDto>();

        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<WorkstationCommandDto>(
                permission.Error ?? "Project write permission is required.", ErrorKind.Forbidden);

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

