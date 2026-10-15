namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Entities.Tasks;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

/// <summary>Finishes a task as done, failed, skipped, or partial, including review-proof and actor-access checks.</summary>
public class FinishWorkHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public FinishWorkHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    /// <summary>
    /// <c>done</c> requires a review run id for a run of this task that is not failed and is check proof, plus review notes or output. Unfixed regressions block done.
    /// The actor id string must be a project member, a system admin, or match <c>BEACON_WORKER_TOKEN</c> in constant time (remark: the HTTP layer supplies the principal user id, not a body field).
    /// </summary>
    public async Task<Result> HandleAsync(FinishWorkCommand command, CancellationToken ct = default)
    {
        if (!Guid.TryParse(command.Request.TaskId, out var taskId))
            return Result.Failure("Invalid task ID format.");

        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);

        if (task is null)
            return Result.Failure("Task not found.");

        if (!await ActorHasAccess(db, command.Request.ActorId, task.ProjectId, ct))
            return Result.Failure("Actor does not have access to this project.");

        var resultType = command.Request.Result.ToLowerInvariant();
        var expectedStatus = task.Status;
        var guardedTransition = resultType is "done" or "failed" or "error";
        switch (resultType)
        {
            case "done":
                if (command.Request.Review is { } review
                    && review.RegressionsFound > review.RegressionsFixed)
                    return Result.Failure("Unfixed regressions remain; cannot mark done.");

                if (command.Request.ReviewRunId is null)
                    return Result.Failure("done requires a completed review run.");

                var reviewRun = await FindReviewRun(db, taskId, command.Request, ct);
                if (reviewRun is null)
                    return Result.Failure("Review run not found.");
                if (reviewRun.Status == ReviewRunStatus.Failed)
                    return Result.Failure("Review run did not pass.");
                if (!reviewRun.IsCheckProof())
                    return Result.Failure("Review run is not check proof.");

                var notes = command.Request.Review is null
                    ? (command.Request.Output ?? "").Trim()
                    : FormatReview(command.Request.Review, command.Request.Output);
                if (string.IsNullOrWhiteSpace(notes))
                    return Result.Failure("done requires review notes or output.");
                try
                {
                    task.SetReviewNotes(notes);
                    if (task.Status == TaskItemStatus.Todo)
                        task.MoveToNextStatus();
                    if (task.Status == TaskItemStatus.InProgress)
                        task.MoveToNextStatus();
                    if (task.Status != TaskItemStatus.Done)
                        return Result.Failure("Cannot complete task: status is not Done.");
                }
                catch (InvalidOperationException ex)
                {
                    return Result.Failure($"Cannot complete task: {ex.Message}");
                }
                break;

            case "failed":
            case "error":
                task.Update(description: AppendNote(task.Description, "Failed: " + (command.Request.Output ?? "No output provided")));
                task.ResetToTodo();
                break;

            case "skipped":
            case "partial":
                // No state transition: the description append is not guarded.
                await db.Tasks
                    .Where(t => t.Id == taskId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.Description,
                            AppendNote(task.Description, "Note: " + (command.Request.Output ?? "No output provided"))), ct);
                break;

            default:
                return Result.Failure("Invalid result type. Use 'done', 'failed', 'skipped', or 'partial'.");
        }

        if (guardedTransition)
        {
            // Guarded transition: only takes effect while the status is still the one validated above.
            var updated = await db.Tasks
                .Where(t => t.Id == taskId && t.Status == expectedStatus)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, task.Status)
                    .SetProperty(t => t.CompletedAt, task.CompletedAt)
                    .SetProperty(t => t.ReviewNotes, task.ReviewNotes)
                    .SetProperty(t => t.Description, task.Description), ct);
            if (updated == 0)
                return Result.Failure("Task state changed while finishing; retry.");
        }

        if (Guid.TryParse(command.Request.ActorId, out var userId))
        {
            db.TaskComments.Add(TaskComment.Create(
                $"Work finished: {command.Request.Result}" +
                (command.Request.Output != null ? $"\nOutput: {command.Request.Output}" : ""),
                taskId,
                userId));
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    private static async Task<Domain.Entities.Evals.ReviewRun?> FindReviewRun(
        IBeaconDb db, Guid taskId, FinishWorkRequest request, CancellationToken ct)
    {
        if (request.ReviewRunId is not Guid reviewRunId)
            return null;

        return await db.ReviewRuns.FirstOrDefaultAsync(r => r.Id == reviewRunId && r.TaskId == taskId, ct);
    }

    private static async Task<bool> ActorHasAccess(IBeaconDb db, string actorId, Guid projectId, CancellationToken ct)
    {
        var workerToken = Environment.GetEnvironmentVariable("BEACON_WORKER_TOKEN");
        if (!string.IsNullOrEmpty(workerToken) && FixedTimeEquals(actorId, workerToken))
            return true;

        if (!Guid.TryParse(actorId, out var userId))
            return false;

        return await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct)
            || await db.Users.AnyAsync(u => u.Id == userId && u.IsAdmin, ct);
    }

    private static string FormatReview(FinishWorkReview review, string? output)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"reviewer_run={review.ReviewerRun}");
        sb.AppendLine($"regressions_found={review.RegressionsFound}");
        sb.AppendLine($"regressions_fixed={review.RegressionsFixed}");
        if (!string.IsNullOrWhiteSpace(output))
            sb.AppendLine(output);
        return sb.ToString().Trim();
    }

    private static string AppendNote(string? current, string note)
        => string.IsNullOrEmpty(current) ? note : current + "\n\n" + note;

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}