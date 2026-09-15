namespace ProjectBeacon.Application.Projects;

using Application.Common;
using Application.Tasks;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class ClaimTaskHandler : ICommandHandler<ClaimTaskCommand, Result<TaskItemDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ClaimTaskHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(ClaimTaskCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var taskId = command.Request.TaskId;
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var task = await db.Tasks
                .FromSqlRaw(
                    "SELECT * FROM \"Tasks\" WHERE \"Id\" = {0} AND \"Status\" = 'Todo' LIMIT 1 FOR UPDATE SKIP LOCKED",
                    taskId)
                .FirstOrDefaultAsync(ct);

            if (task is null)
            {
                await tx.RollbackAsync(ct);
                return Result.Failure<TaskItemDto>("Task not found or already claimed.");
            }

            task.MoveToNextStatus();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Result.Ok(MapToDto(task));
        });
    }

    private static TaskItemDto MapToDto(TaskItem task) =>
        new(
            task.Id,
            task.Title,
            task.Description,
            task.Status.ToString(),
            task.Priority,
            task.Type,
            task.Status == TaskItemStatus.InProgress ? task.SubStage : null,
            task.ProjectId,
            task.LabelId,
            task.MilestoneId,
            task.ReviewNotes,
            task.CreatedAt,
            task.CompletedAt,
            [],
            []);
}