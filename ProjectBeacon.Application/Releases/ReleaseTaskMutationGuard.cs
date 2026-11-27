namespace ProjectBeacon.Application.Releases;

using Application.Persistence;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Freezes the task scope captured by ReleasePrepare. A task linked to a milestone in a
/// prepared/candidate/published Release cannot be edited, deleted, reassigned, or started
/// in a new pipeline. A failed preparation can be unlocked only after the Release is
/// explicitly cancelled.
/// </summary>
public static class ReleaseTaskMutationGuard
{
    public static async Task<string?> GetBlockReasonAsync(
        IBeaconDb db,
        Guid projectId,
        IEnumerable<Guid?> milestoneIds,
        CancellationToken ct = default)
    {
        var ids = milestoneIds
            .Where(id => id is not null && id != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (projectId == Guid.Empty || ids.Length == 0)
            return null;

        var releaseIds = await db.Milestones.AsNoTracking()
            .Where(milestone => milestone.ProjectId == projectId
                && ids.Contains(milestone.Id)
                && milestone.ReleaseId != null)
            .Select(milestone => milestone.ReleaseId!.Value)
            .Distinct()
            .ToArrayAsync(ct);
        if (releaseIds.Length == 0)
            return null;

        var blocked = await db.Releases.AsNoTracking()
            .Where(release => release.ProjectId == projectId
                && releaseIds.Contains(release.Id)
                && release.PreparationStatus != ReleasePreparationStatus.NotStarted
                && !(release.Status == ReleaseStatus.Cancelled
                    && release.PreparationStatus == ReleasePreparationStatus.Failed))
            .OrderBy(release => release.Order)
            .ThenBy(release => release.Id)
            .Select(release => new { release.Name, release.PreparationStatus })
            .FirstOrDefaultAsync(ct);

        return blocked is null
            ? null
            : $"This task belongs to Release '{blocked.Name}', whose preparation has started ({blocked.PreparationStatus}). Its scope is frozen. Cancel a failed Release before changing the task, or create a new task for a later Release.";
    }
}


/// <summary>
/// Freezes the fields and membership of a milestone captured by ReleasePrepare.
/// The project mutation lock must be held by the caller for the duration of the
/// surrounding transaction so this check cannot race Candidate snapshot creation.
/// </summary>
public static class ReleaseMilestoneMutationGuard
{
    public static async Task<string?> GetBlockReasonAsync(
        IBeaconDb db,
        Guid projectId,
        Guid milestoneId,
        CancellationToken ct = default)
    {
        if (projectId == Guid.Empty || milestoneId == Guid.Empty)
            return null;

        var releaseId = await db.Milestones.AsNoTracking()
            .Where(milestone => milestone.ProjectId == projectId && milestone.Id == milestoneId)
            .Select(milestone => milestone.ReleaseId)
            .FirstOrDefaultAsync(ct);
        if (releaseId is null)
            return null;

        var blocked = await db.Releases.AsNoTracking()
            .Where(release => release.ProjectId == projectId
                && release.Id == releaseId.Value
                && release.PreparationStatus != ReleasePreparationStatus.NotStarted
                && !(release.Status == ReleaseStatus.Cancelled
                    && release.PreparationStatus == ReleasePreparationStatus.Failed))
            .Select(release => new { release.Name, release.PreparationStatus })
            .FirstOrDefaultAsync(ct);

        return blocked is null
            ? null
            : $"This milestone belongs to Release '{blocked.Name}', whose preparation has started ({blocked.PreparationStatus}). Its Candidate scope is frozen. Cancel a failed Release before changing the milestone.";
    }
}
