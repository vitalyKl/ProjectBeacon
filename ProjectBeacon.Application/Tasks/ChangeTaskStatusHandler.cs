namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Entities.Tasks;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Moves a task to Todo, InProgress, or Done. Done fails unless a completed review run exists.
/// </summary>
public class ChangeTaskStatusHandler : ICommandHandler<ChangeTaskStatusCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ChangeTaskStatusHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(ChangeTaskStatusCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        IQueryable<TaskItem> query = db.Tasks.AsNoTracking();
        if (command.Request.ProjectId is Guid projectId)
            query = query.Where(t => t.ProjectId == projectId);
        var task = await query.FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<TaskItemDto>("Task not found.");

        if (command.Request.Status == TaskItemStatus.Done)
        {
            var runs = await db.ReviewRuns.Where(r => r.TaskId == task.Id).ToListAsync(ct);
            var proved = runs.Any(r => r.IsCheckProof());
            if (!proved)
                return Result.Failure<TaskItemDto>("done requires a completed review run.");
        }

        var expectedStatus = task.Status;
        try
        {
            task.TransitionTo(command.Request.Status);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<TaskItemDto>(ex.Message);
        }

        // Guarded transition: only takes effect while the status is still the one validated above.
        var updated = await db.Tasks
            .Where(t => t.Id == task.Id && t.Status == expectedStatus)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, task.Status)
                .SetProperty(t => t.CompletedAt, task.CompletedAt), ct);
        if (updated == 0)
            return Result.Failure<TaskItemDto>("Task state changed while transitioning; retry.");

        return Result.Ok(new TaskItemDto(
            task.Id, task.Title, task.Description, task.Status.ToString(),
            task.Priority, task.Type,
            task.Status == TaskItemStatus.InProgress ? task.SubStage : null,
            task.ProjectId, task.LabelId, task.MilestoneId, task.ReviewNotes,
            task.CreatedAt, task.CompletedAt, [], []));
    }
}