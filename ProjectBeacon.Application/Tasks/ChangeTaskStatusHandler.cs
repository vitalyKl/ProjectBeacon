namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class ChangeTaskStatusHandler : ICommandHandler<ChangeTaskStatusCommand, Result<TaskItemDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ChangeTaskStatusHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(ChangeTaskStatusCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var query = db.Tasks.AsQueryable();
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
            await db.Entry(task).ReloadAsync(ct);
            return Result.Failure<TaskItemDto>(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(new TaskItemDto(
            task.Id, task.Title, task.Description, task.Status.ToString(),
            task.Priority, task.Type,
            task.Status == TaskItemStatus.InProgress ? task.SubStage : null,
            task.ProjectId, task.LabelId, task.MilestoneId, task.ReviewNotes,
            task.CreatedAt, task.CompletedAt, [], []));
    }
}