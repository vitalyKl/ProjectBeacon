namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Devices;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Queues a repository operation on the most recently active online project workstation.</summary>
public sealed class EnqueueProjectGitSyncHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly TimeProvider _timeProvider;

    public EnqueueProjectGitSyncHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        var permission = await ProjectGitSyncAuthorization.CanWriteAsync(
            db, actor, userId, _authorization, projectId, ct);
        if (!permission.Success)
            return Result.Failure<WorkstationCommandDto>(
                permission.Error ?? "Project write permission is required.", ErrorKind.Forbidden);

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
            .Where(candidate => candidate.Device.IsOnline(now))
            .OrderByDescending(candidate => candidate.Device.LastHeartbeatAt)
            .FirstOrDefault();

        if (selected is null)
            return Result.Failure<WorkstationCommandDto>(
                "No online workstation is attached to this project.", ErrorKind.Conflict);

        var payload = JsonSerializer.Serialize(new { projectId, mode = mode.ToString() });
        var command = WorkstationCommand.Create(
            selected.Device.Id,
            WorkstationCommandKind.GitSync,
            payload,
            projectId,
            selected.Device.UserId);
        db.WorkstationCommands.Add(command);
        await db.SaveChangesAsync(ct);

        // Audit event: connect this human-requested operation to the project audit stream
        // when the audit infrastructure is available in the target branch.
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
        var permission = await ProjectGitSyncAuthorization.CanWriteAsync(
            db, actor, userId, _authorization, projectId, ct);
        if (!permission.Success)
            return Result.Failure<WorkstationCommandDto>(
                permission.Error ?? "Project write permission is required.", ErrorKind.Forbidden);

        var command = await db.WorkstationCommands.IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Id == commandId
                && candidate.ProjectId == projectId
                && candidate.Kind == WorkstationCommandKind.GitSync, ct);
        if (command is null)
            return Result.Failure<WorkstationCommandDto>("GitSync command not found.", ErrorKind.NotFound);

        // Project members can inspect the command result, but the workstation's local
        // filesystem path is never disclosed through a human-facing endpoint.
        return Result.Ok(EnqueueCommandHandler.MapCommand(command));
    }
}

internal static class ProjectGitSyncAuthorization
{
    public static async Task<Result> CanWriteAsync(
        IBeaconDb db,
        ActorContext actor,
        Guid userId,
        IAuthorizationService authorization,
        Guid projectId,
        CancellationToken ct)
    {
        var isMember = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(member => member.ProjectId == projectId && member.UserId == userId, ct);
        if (isMember)
            return Result.Ok();

        return await authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
    }
}
