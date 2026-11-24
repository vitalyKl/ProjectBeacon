namespace ProjectBeacon.Application.Devices;

using Application.Common;
using Application.Persistence;
using Application.Projects;
using Domain.Common;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Returns only AutoSync-enabled roots already bound to the authenticated device.
/// The device token is the authorization boundary; no arbitrary project/path can be requested.
/// </summary>
public sealed class GetDeviceAutoSyncTargetsHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetDeviceAutoSyncTargetsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IReadOnlyList<DeviceAutoSyncTargetDto>>> HandleAsync(
        Guid deviceId, CancellationToken ct = default)
    {
        if (deviceId == Guid.Empty)
            return Result.Failure<IReadOnlyList<DeviceAutoSyncTargetDto>>(
                "Device not found.", ErrorKind.NotFound);

        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == deviceId, ct);
        if (device is null || device.IsRevoked)
            return Result.Failure<IReadOnlyList<DeviceAutoSyncTargetDto>>(
                "Device not found.", ErrorKind.NotFound);
        if (!WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.AutoSyncV1))
            return Result.Ok((IReadOnlyList<DeviceAutoSyncTargetDto>)Array.Empty<DeviceAutoSyncTargetDto>());

        var runtimes = await (
            from runtime in db.ProjectRuntimes.IgnoreQueryFilters().AsNoTracking()
            join project in db.Projects.IgnoreQueryFilters().AsNoTracking()
                on runtime.ProjectId equals project.Id
            where runtime.DeviceId == deviceId
            select new
            {
                runtime.ProjectId,
                runtime.LocalRoot,
                project.GitPolicyJson,
                project.GitPolicyRevision
            }).ToListAsync(ct);

        var targets = new List<DeviceAutoSyncTargetDto>();
        foreach (var runtime in runtimes)
        {
            if (string.IsNullOrWhiteSpace(runtime.LocalRoot)
                || !ProjectGitPolicyCatalog.TryRead(
                    runtime.GitPolicyJson, runtime.GitPolicyRevision, out var policy)
                || !policy.AutoSyncEnabled)
                continue;

            targets.Add(new DeviceAutoSyncTargetDto(
                runtime.ProjectId, runtime.LocalRoot, policy.Revision));
        }

        return Result.Ok((IReadOnlyList<DeviceAutoSyncTargetDto>)targets);
    }
}

public sealed record DeviceAutoSyncTargetDto(
    Guid ProjectId,
    string LocalRoot,
    int PolicyRevision);
