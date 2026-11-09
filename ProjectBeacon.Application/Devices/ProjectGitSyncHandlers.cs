namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using Application.Authorization;
using Application.Common;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Queues a repository operation on the project's most recently active online workstation.</summary>
public sealed class EnqueueProjectGitSyncHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly EnqueueCommandHandler _enqueue;
    private readonly IAuthorizationService _authorization;
    private readonly TimeProvider _timeProvider;

    public EnqueueProjectGitSyncHandler(
        IBeaconDbFactory dbFactory,
        EnqueueCommandHandler enqueue,
        IAuthorizationService authorization,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _enqueue = enqueue;
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
            return Result.Forbidden();
        if (!Enum.IsDefined(mode))
            return Result.Failure<WorkstationCommandDto>("Unsupported GitSync mode.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
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
        // Audit event: connect this operation to the project audit stream when that
        // infrastructure is available in the target branch.
        return await _enqueue.HandleAsync(new EnqueueCommandCommand(new EnqueueCommandRequest(
            selected.Device.Id,
            userId,
            WorkstationCommandKind.GitSync,
            payload,
            projectId)), ct);
    }
}
