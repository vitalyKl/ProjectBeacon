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


/// <summary>
/// Serializes mutations of task data captured by ReleasePrepare with the immutable
/// Candidate snapshot. The project row lock is shared with ReleasePrepare and is held
/// until the task/step mutation transaction commits.
/// </summary>
internal static class ReleaseScopeMutationCoordinator
{
    public static async Task<Result<T>> ExecuteForProjectAsync<T>(
        IBeaconDbFactory dbFactory,
        Guid projectId,
        IEnumerable<Guid?> milestoneIds,
        Func<IBeaconDb, CancellationToken, Task<Result<T>>> mutation,
        CancellationToken ct)
    {
        if (projectId == Guid.Empty)
            return Result.Failure<T>("Project id is required.", ErrorKind.Validation);

        await using var db = dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await ProjectRuntimeMutationGate.TryLockProjectAsync(db, projectId, ct))
                return Result.Failure<T>("Project not found.", ErrorKind.NotFound);

            var blockReason = await ReleaseTaskMutationGuard.GetBlockReasonAsync(
                db, projectId, milestoneIds, ct);
            if (blockReason is not null)
                return Result.Failure<T>(blockReason, ErrorKind.Conflict);

            var result = await mutation(db, ct);
            if (!result.Success)
                return result;

            await transaction.CommitAsync(ct);
            return result;
        });
    }

    public static async Task<Result<T>> ExecuteForTaskAsync<T>(
        IBeaconDbFactory dbFactory,
        Guid taskId,
        IEnumerable<Guid?>? additionalMilestoneIds,
        Func<IBeaconDb, TaskItem, CancellationToken, Task<Result<T>>> mutation,
        CancellationToken ct)
    {
        if (taskId == Guid.Empty)
            return Result.Failure<T>("Task id is required.", ErrorKind.Validation);

        await using var db = dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // ProjectId is immutable for a TaskItem. Resolve it first, take the shared
            // project lock, then reload the task and evaluate Candidate membership.
            var projectId = await db.Tasks.IgnoreQueryFilters().AsNoTracking()
                .Where(task => task.Id == taskId)
                .Select(task => (Guid?)task.ProjectId)
                .FirstOrDefaultAsync(ct);
            if (projectId is null)
                return Result.Failure<T>("Task not found.", ErrorKind.NotFound);
            if (!await ProjectRuntimeMutationGate.TryLockProjectAsync(db, projectId.Value, ct))
                return Result.Failure<T>("Task not found.", ErrorKind.NotFound);

            var task = await db.Tasks.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == taskId && item.ProjectId == projectId.Value, ct);
            if (task is null)
                return Result.Failure<T>("Task not found.", ErrorKind.NotFound);

            var milestoneIds = new List<Guid?> { task.MilestoneId };
            if (additionalMilestoneIds is not null)
                milestoneIds.AddRange(additionalMilestoneIds);
            var blockReason = await ReleaseTaskMutationGuard.GetBlockReasonAsync(
                db, task.ProjectId, milestoneIds, ct);
            if (blockReason is not null)
                return Result.Failure<T>(blockReason, ErrorKind.Conflict);

            var result = await mutation(db, task, ct);
            if (!result.Success)
                return result;

            await transaction.CommitAsync(ct);
            return result;
        });
    }

    public static async Task<Result<T>> ExecuteForStepAsync<T>(
        IBeaconDbFactory dbFactory,
        Guid stepId,
        Func<IBeaconDb, TaskItem, TaskStep, CancellationToken, Task<Result<T>>> mutation,
        CancellationToken ct)
    {
        if (stepId == Guid.Empty)
            return Result.Failure<T>("Step id is required.", ErrorKind.Validation);

        await using var db = dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var scope = await db.TaskSteps.IgnoreQueryFilters().AsNoTracking()
                .Where(step => step.Id == stepId)
                .Select(step => new { step.TaskId, step.ProjectId })
                .FirstOrDefaultAsync(ct);
            if (scope is null)
                return Result.Failure<T>("Step not found.", ErrorKind.NotFound);

            if (!await ProjectRuntimeMutationGate.TryLockProjectAsync(db, scope.ProjectId, ct))
                return Result.Failure<T>("Step not found.", ErrorKind.NotFound);

            var task = await db.Tasks.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == scope.TaskId && item.ProjectId == scope.ProjectId, ct);
            var step = await db.TaskSteps.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == stepId
                    && item.TaskId == scope.TaskId && item.ProjectId == scope.ProjectId, ct);
            if (task is null || step is null)
                return Result.Failure<T>("Step or its task was not found.", ErrorKind.NotFound);

            var blockReason = await ReleaseTaskMutationGuard.GetBlockReasonAsync(
                db, task.ProjectId, [task.MilestoneId], ct);
            if (blockReason is not null)
                return Result.Failure<T>(blockReason, ErrorKind.Conflict);

            var result = await mutation(db, task, step, ct);
            if (!result.Success)
                return result;

            await transaction.CommitAsync(ct);
            return result;
        });
    }
}
