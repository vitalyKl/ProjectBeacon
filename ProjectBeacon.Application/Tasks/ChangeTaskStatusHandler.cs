namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Application.Releases;
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
        return await ReleaseScopeMutationCoordinator.ExecuteForTaskAsync(
            _dbFactory,
            command.Request.TaskId,
            null,
            async (db, task, token) =>
            {
                if (command.Request.ProjectId is { } projectId && projectId != task.ProjectId)
                    return Result.Failure<TaskItemDto>("Task not found.", ErrorKind.NotFound);

                if (command.Request.Status == TaskItemStatus.Done)
                {
                    var runs = await db.ReviewRuns
                        .Where(run => run.TaskId == task.Id)
                        .ToListAsync(token);
                    if (!runs.Any(run => run.IsCheckProof()))
                        return Result.Failure<TaskItemDto>("done requires a completed review run.");
                }

                var expectedStatus = task.Status;
                try
                {
                    task.TransitionTo(command.Request.Status);
                }
                catch (InvalidOperationException exception)
                {
                    return Result.Failure<TaskItemDto>(exception.Message);
                }

                // Keep the optimistic same-row predicate for duplicate finish-work
                // requests while the project lock serializes this write with ReleasePrepare.
                var updated = await db.Tasks
                    .Where(item => item.Id == task.Id && item.Status == expectedStatus)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.Status, task.Status)
                        .SetProperty(item => item.CompletedAt, task.CompletedAt), token);
                if (updated == 0)
                {
                    var current = await db.Tasks.AsNoTracking()
                        .FirstOrDefaultAsync(item => item.Id == task.Id, token);
                    if (current is not null && current.Status == command.Request.Status)
                        return Result.Ok(MapToDto(current));
                    return Result.Failure<TaskItemDto>(
                        "Task state changed while transitioning; retry.", ErrorKind.Conflict);
                }

                return Result.Ok(MapToDto(task));
            },
            ct);
    }

    private static TaskItemDto MapToDto(TaskItem task) =>
        new(
            task.Id, task.Title, task.Description, task.Status.ToString(),
            task.Priority, task.Type,
            task.Status == TaskItemStatus.InProgress ? task.SubStage : null,
            task.ProjectId, task.LabelId, task.MilestoneId, task.ReviewNotes,
            task.CreatedAt, task.CompletedAt, [], [], task.ContentRevision);
}
