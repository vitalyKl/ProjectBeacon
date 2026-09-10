namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class ChangeTaskStatusHandler : ICommandHandler<ChangeTaskStatusCommand, Result<TaskItemDto>>
{
    private readonly BeaconDbContext _db;

    public ChangeTaskStatusHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<TaskItemDto>> HandleAsync(ChangeTaskStatusCommand command, CancellationToken ct = default)
    {
        var query = _db.Tasks.AsQueryable();
        if (command.Request.ProjectId is Guid projectId)
            query = query.Where(t => t.ProjectId == projectId);
        var task = await query.FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<TaskItemDto>("Task not found.");

        try
        {
            task.TransitionTo(command.Request.Status);
        }
        catch (InvalidOperationException ex)
        {
            await _db.Entry(task).ReloadAsync(ct);
            return Result.Failure<TaskItemDto>(ex.Message);
        }

        await _db.SaveChangesAsync(ct);
        return Result.Ok(new TaskItemDto(
            task.Id, task.Title, task.Description, task.Status.ToString(),
            task.Priority, task.Type,
            task.Status == TaskItemStatus.InProgress ? task.SubStage : null,
            task.ProjectId, task.LabelId, task.MilestoneId, task.ReviewNotes,
            task.CreatedAt, task.CompletedAt, [], []));
    }
}
