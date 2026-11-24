namespace ProjectBeacon.Application.Devices;

using Application.Common;
using Application.Persistence;
using Domain.Common;
using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Supplies a task's server-authorized workspace bootstrap. For Hotfix tasks, the branch
/// and commit come from the published Release proof, never from client input or local HEAD.
/// </summary>
public sealed class GetDeviceTaskWorkspaceBootstrapHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetDeviceTaskWorkspaceBootstrapHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<DeviceTaskWorkspaceBootstrapDto>> HandleAsync(
        Guid deviceId, Guid taskId, CancellationToken ct = default)
    {
        if (deviceId == Guid.Empty || taskId == Guid.Empty)
            return Result.Failure<DeviceTaskWorkspaceBootstrapDto>(
                "Device and task ids are required.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var device = await db.DaemonDevices.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == deviceId, ct);
        if (device is null || device.IsRevoked)
            return Result.Failure<DeviceTaskWorkspaceBootstrapDto>(
                "Device not found.", ErrorKind.NotFound);

        var task = await db.Tasks.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == taskId, ct);
        if (task is null || task.PipelineDeviceId != deviceId)
            return Result.Failure<DeviceTaskWorkspaceBootstrapDto>(
                "Task not found on this workstation.", ErrorKind.NotFound);

        var runtime = await db.ProjectRuntimes.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(item => item.ProjectId == task.ProjectId
                && item.DeviceId == deviceId, ct);
        if (runtime is null || string.IsNullOrWhiteSpace(runtime.LocalRoot))
            return Result.Failure<DeviceTaskWorkspaceBootstrapDto>(
                "The project is not attached to this workstation.", ErrorKind.Conflict);

        if (task.HotfixSourceReleaseId is null)
            return Result.Ok(new DeviceTaskWorkspaceBootstrapDto(
                task.ProjectId, task.Id, false, null, null, null));

        if (string.IsNullOrWhiteSpace(task.HotfixDeployedSha)
            || string.IsNullOrWhiteSpace(task.HotfixTargetBranch)
            || !IsGitSha(task.HotfixDeployedSha)
            || !ProjectVersionPolicyCatalog.IsSafeBranchName(task.HotfixTargetBranch))
            return Result.Failure<DeviceTaskWorkspaceBootstrapDto>(
                "Hotfix task origin is incomplete or invalid. Refusing to use a local checkout as fallback.",
                ErrorKind.Conflict);

        if (!WorkstationCapabilities.Supports(device.ProbeJson, WorkstationCapabilities.GitLandV1))
            return Result.Failure<DeviceTaskWorkspaceBootstrapDto>(
                "The pinned workstation does not support verified task landing.", ErrorKind.Conflict);

        var release = await db.Releases.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == task.HotfixSourceReleaseId
                && item.ProjectId == task.ProjectId, ct);
        if (release is null
            || release.Status != Domain.Enums.ReleaseStatus.Published
            || release.PublicationStatus != Domain.Enums.ReleasePublicationStatus.Published
            || !release.PublicationTagPushed
            || release.PreparationDeviceId != deviceId
            || release.CandidateId is not { } candidateId
            || !string.Equals(release.DeployedCommitSha, task.HotfixDeployedSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(release.TargetBranch, task.HotfixTargetBranch, StringComparison.Ordinal))
            return Result.Failure<DeviceTaskWorkspaceBootstrapDto>(
                "Hotfix no longer matches its immutable production deployment proof.", ErrorKind.Conflict);

        var currentDeployedReleaseId = await db.Releases.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.ProjectId == task.ProjectId
                && item.Status == Domain.Enums.ReleaseStatus.Published
                && item.PublicationStatus == Domain.Enums.ReleasePublicationStatus.Published
                && item.PublicationTagPushed
                && item.DeployedAtUtc != null
                && item.DeploymentWorkflowRunId != null)
            .OrderByDescending(item => item.DeployedAtUtc)
            .ThenByDescending(item => item.PublishedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => item.Id)
            .FirstOrDefaultAsync(ct);
        if (currentDeployedReleaseId != release.Id)
            return Result.Failure<DeviceTaskWorkspaceBootstrapDto>(
                "A newer production Release has been deployed since this Hotfix was created. Refresh its base from the current deployment before continuing.",
                ErrorKind.Conflict);

        var candidate = await db.ReleaseCandidateSnapshots.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(item => item.ProjectId == task.ProjectId
                && item.ReleaseId == release.Id
                && item.CandidateId == candidateId
                && item.PreparedByDeviceId == deviceId, ct);
        if (candidate is null
            || !string.Equals(candidate.CandidateSha, task.HotfixDeployedSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.TargetBranch, task.HotfixTargetBranch, StringComparison.Ordinal)
            || !string.Equals(release.DeployedVersion, candidate.Version, StringComparison.Ordinal)
            || !string.Equals(release.PublishedTagName, candidate.TagName, StringComparison.Ordinal))
            return Result.Failure<DeviceTaskWorkspaceBootstrapDto>(
                "Hotfix Candidate evidence does not match the deployed commit, tag, version and target branch.",
                ErrorKind.Conflict);

        var policyJson = await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .Where(project => project.Id == task.ProjectId)
            .Select(project => project.VersionPolicyJson)
            .FirstOrDefaultAsync(ct);
        if (!ProjectVersionPolicyCatalog.TryRead(policyJson, out var policy, out var policyError)
            || !policy.ReleaseManagementEnabled)
            return Result.Failure<DeviceTaskWorkspaceBootstrapDto>(
                policyError ?? "Release Management is no longer enabled for this project.",
                ErrorKind.Conflict);

        return Result.Ok(new DeviceTaskWorkspaceBootstrapDto(
            task.ProjectId, task.Id, true, task.HotfixSourceReleaseId,
            task.HotfixTargetBranch, task.HotfixDeployedSha));
    }

    private static bool IsGitSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}

public sealed record DeviceTaskWorkspaceBootstrapDto(
    Guid ProjectId,
    Guid TaskId,
    bool IsHotfix,
    Guid? SourceReleaseId,
    string? BaseBranch,
    string? BaseSha);
