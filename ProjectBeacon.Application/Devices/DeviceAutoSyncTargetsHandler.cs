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

            var remoteLandings = await ListRemoteLandingTargetsAsync(
                db, runtime.ProjectId, deviceId, ct);
            targets.Add(new DeviceAutoSyncTargetDto(
                runtime.ProjectId, runtime.LocalRoot, policy.Revision, remoteLandings));
        }

        return Result.Ok((IReadOnlyList<DeviceAutoSyncTargetDto>)targets);
    }
    private static async Task<IReadOnlyList<DeviceRemoteLandingTargetDto>> ListRemoteLandingTargetsAsync(
        IBeaconDb db, Guid projectId, Guid deviceId, CancellationToken ct)
    {
        var taskIds = await db.Tasks.IgnoreQueryFilters().AsNoTracking()
            .Where(task => task.ProjectId == projectId
                && task.PipelineDeviceId == deviceId
                && task.Status == Domain.Enums.TaskItemStatus.InProgress
                && task.PipelineStage == Domain.Enums.TaskPipelineStage.Approved)
            .Select(task => task.Id)
            .ToArrayAsync(ct);

        var targets = new List<DeviceRemoteLandingTargetDto>();
        foreach (var taskId in taskIds)
        {
            var activeSession = await db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(session => session.ProjectId == projectId
                    && session.TaskId == taskId
                    && session.Status is Domain.Enums.SessionStatus.Ready
                        or Domain.Enums.SessionStatus.Active
                        or Domain.Enums.SessionStatus.WaitingForUser, ct);
            if (activeSession)
                continue;

            var review = await db.ReviewRuns.IgnoreQueryFilters().AsNoTracking()
                .Where(run => run.ProjectId == projectId && run.TaskId == taskId)
                .OrderByDescending(run => run.StartedAt)
                .ThenByDescending(run => run.Id)
                .FirstOrDefaultAsync(ct);
            if (review is null || !review.IsLandingProof()
                || review.CheckedDeviceId != deviceId
                || string.IsNullOrWhiteSpace(review.CheckedBaseBranch)
                || string.IsNullOrWhiteSpace(review.CheckedSourceSha))
                continue;

            var verdict = await db.ReviewVerdicts.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.ProjectId == projectId && item.TaskId == taskId)
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .FirstOrDefaultAsync(ct);
            if (verdict is null || verdict.Kind != Domain.Enums.ReviewVerdictKind.Approve)
                continue;

            targets.Add(new DeviceRemoteLandingTargetDto(
                taskId,
                review.Id,
                $"beacon/task/{taskId:N}",
                review.CheckedBaseBranch,
                review.CheckedSourceSha));
        }

        return targets;
    }
}

public sealed record DeviceAutoSyncTargetDto(
    Guid ProjectId,
    string LocalRoot,
    int PolicyRevision,
    IReadOnlyList<DeviceRemoteLandingTargetDto>? RemoteLandings = null);

public sealed record DeviceRemoteLandingTargetDto(
    Guid TaskId,
    Guid ReviewRunId,
    string SourceBranch,
    string TargetBranch,
    string SourceSha);
