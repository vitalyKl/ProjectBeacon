namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

public class FinishWorkHandler
{
    private readonly BeaconDbContext _db;

    public FinishWorkHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<bool>> HandleAsync(FinishWorkCommand command, CancellationToken ct = default)
    {
        if (!Guid.TryParse(command.Request.TaskId, out var taskId))
            return Result.Failure<bool>("Invalid task ID format.");

        var task = await _db.Tasks
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);

        if (task is null)
            return Result.Failure<bool>("Task not found.");

        if (!await ActorHasAccess(command.Request.ActorId, task.ProjectId, ct))
            return Result.Failure<bool>("Actor does not have access to this project.");

        var resultType = command.Request.Result.ToLowerInvariant();
        switch (resultType)
        {
            case "done":
                if (command.Request.Review is null || !command.Request.Review.ReviewerRun)
                    return Result.Failure<bool>("done requires a structured review (reviewer_run, regressions_found, regressions_fixed).");

                if (command.Request.Review.RegressionsFound > command.Request.Review.RegressionsFixed)
                    return Result.Failure<bool>("Unfixed regressions remain; cannot mark done.");

                var notes = FormatReview(command.Request.Review, command.Request.Output);
                try
                {
                    task.SetReviewNotes(notes);
                    if (task.Status == TaskItemStatus.Todo)
                        task.MoveToNextStatus();
                    if (task.Status == TaskItemStatus.InProgress)
                        task.MoveToNextStatus();
                    if (task.Status != TaskItemStatus.Done)
                        return Result.Failure<bool>("Cannot complete task: status is not Done.");
                }
                catch (InvalidOperationException ex)
                {
                    return Result.Failure<bool>($"Cannot complete task: {ex.Message}");
                }
                break;

            case "failed":
            case "error":
                task.Update(description: AppendNote(task.Description, "Failed: " + (command.Request.Output ?? "No output provided")));
                task.ResetToTodo();
                break;

            case "skipped":
            case "partial":
                task.Update(description: AppendNote(task.Description, "Note: " + (command.Request.Output ?? "No output provided")));
                break;

            default:
                return Result.Failure<bool>("Invalid result type. Use 'done', 'failed', 'skipped', or 'partial'.");
        }

        if (Guid.TryParse(command.Request.ActorId, out var userId))
        {
            _db.TaskComments.Add(TaskComment.Create(
                $"Work finished: {command.Request.Result}" +
                (command.Request.Output != null ? $"\nOutput: {command.Request.Output}" : ""),
                taskId,
                userId));
        }

        await _db.SaveChangesAsync(ct);
        return Result.Ok(true);
    }

    private async Task<bool> ActorHasAccess(string actorId, Guid projectId, CancellationToken ct)
    {
        var workerToken = Environment.GetEnvironmentVariable("BEACON_WORKER_TOKEN");
        if (!string.IsNullOrEmpty(workerToken) && FixedTimeEquals(actorId, workerToken))
            return true;

        if (!Guid.TryParse(actorId, out var userId))
            return false;

        return await _db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct)
            || await _db.Users.AnyAsync(u => u.Id == userId && u.IsAdmin, ct);
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
