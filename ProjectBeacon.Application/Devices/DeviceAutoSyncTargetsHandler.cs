namespace ProjectBeacon.Application.Devices;

using System.Security.Cryptography;
using System.Text;
using Application.Common;
using Application.Persistence;
using Application.Projects;
using Domain.Common;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Returns authenticated device roots that need repository AutoSync and/or exact remote-landing reconciliation.
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
                project.GitPolicyRevision,
                project.ReviewCheckCommand
            }).ToListAsync(ct);

        var targets = new List<DeviceAutoSyncTargetDto>();
        foreach (var runtime in runtimes)
        {
            if (string.IsNullOrWhiteSpace(runtime.LocalRoot)
                || !ProjectGitPolicyCatalog.TryRead(
                    runtime.GitPolicyJson, runtime.GitPolicyRevision, out var policy))
                continue;

            var remoteLandings = await ListRemoteLandingTargetsAsync(
                db, runtime.ProjectId, deviceId, policy.AutoSyncEnabled,
                runtime.ReviewCheckCommand, ct);
            if (!policy.AutoSyncEnabled
                && (remoteLandings.Count == 0
                    || !WorkstationCapabilities.Supports(
                        device.ProbeJson, WorkstationCapabilities.RemoteLandingReconcileV1)))
                continue;

            targets.Add(new DeviceAutoSyncTargetDto(
                runtime.ProjectId, runtime.LocalRoot, policy.Revision,
                policy.AutoSyncEnabled, remoteLandings));
        }

        return Result.Ok((IReadOnlyList<DeviceAutoSyncTargetDto>)targets);
    }
    private static async Task<IReadOnlyList<DeviceRemoteLandingTargetDto>> ListRemoteLandingTargetsAsync(
        IBeaconDb db, Guid projectId, Guid deviceId, bool autoSyncEnabled,
        string? reviewCheckCommand, CancellationToken ct)
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
                    && (session.Status == Domain.Enums.SessionStatus.Ready
                        || session.Status == Domain.Enums.SessionStatus.Active
                        || session.Status == Domain.Enums.SessionStatus.WaitingForUser), ct);
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

            // AutoSync is a separate repository-sync preference. For Beacon-managed
            // GitHub merges, continue reconciling both a fully verified Landed attempt
            // and an async merge request that is pending/in a queue. The latter is never
            // closed here: the pinned workstation must verify MERGED, exact candidate
            // tree and the project post-merge check before the server records Landed.
            var landingAttempt = await db.LandingAttempts.IgnoreQueryFilters().AsNoTracking()
                .Where(attempt => attempt.ProjectId == projectId && attempt.TaskId == taskId)
                .OrderByDescending(attempt => attempt.CreatedAt)
                .ThenByDescending(attempt => attempt.Id)
                .FirstOrDefaultAsync(ct);
            Guid? landingAttemptId = null;
            string? mergeRequestId = null;
            string? candidateTreeSha = null;
            string? postMergeCheckCommand = null;
            if (landingAttempt is not null)
            {
                var proofMatches = landingAttempt.ReviewRunId == review.Id
                    && landingAttempt.DeviceId == deviceId
                    && string.Equals(landingAttempt.SourceSha, review.CheckedSourceSha, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(landingAttempt.TargetBranch, review.CheckedBaseBranch, StringComparison.Ordinal);
                if (!proofMatches)
                    continue;

                if (landingAttempt.Status == Domain.Enums.LandingAttemptStatus.Landed)
                {
                    landingAttemptId = landingAttempt.Id;
                }
                else if (landingAttempt.TryReadGitHubMergeRequestId(out mergeRequestId)
                    && landingAttempt.CandidateTreeSha is { Length: 40 or 64 } candidateTreeValue
                    && candidateTreeValue.All(Uri.IsHexDigit))
                {
                    landingAttemptId = landingAttempt.Id;
                    candidateTreeSha = candidateTreeValue;
                    var configuredCheck = reviewCheckCommand?.Trim();
                    if (!string.IsNullOrWhiteSpace(configuredCheck))
                    {
                        var configuredHash = Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes(configuredCheck))).ToLowerInvariant();
                        if (string.Equals(configuredHash, landingAttempt.ReviewCommandHash, StringComparison.OrdinalIgnoreCase))
                            postMergeCheckCommand = configuredCheck;
                    }
                }
                else
                {
                    continue;
                }
            }
            if (!autoSyncEnabled && landingAttempt is null)
                continue;

            targets.Add(new DeviceRemoteLandingTargetDto(
                taskId,
                review.Id,
                $"beacon/task/{taskId:N}",
                review.CheckedBaseBranch,
                review.CheckedSourceSha,
                landingAttemptId,
                mergeRequestId,
                candidateTreeSha,
                postMergeCheckCommand));
        }

        return targets;
    }
}

public sealed record DeviceAutoSyncTargetDto(
    Guid ProjectId,
    string LocalRoot,
    int PolicyRevision,
    bool AutoSyncEnabled = true,
    IReadOnlyList<DeviceRemoteLandingTargetDto>? RemoteLandings = null);

public sealed record DeviceRemoteLandingTargetDto(
    Guid TaskId,
    Guid ReviewRunId,
    string SourceBranch,
    string TargetBranch,
    string SourceSha,
    Guid? LandingAttemptId = null,
    string? MergeRequestId = null,
    string? CandidateTreeSha = null,
    string? PostMergeCheckCommand = null);
