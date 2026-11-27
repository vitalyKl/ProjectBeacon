namespace ProjectBeacon.Application.Projects;

using Application.Common;
using Application.Devices;
using Application.Releases;
using Application.Tasks;
using Domain.Entities.Tasks;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Moves a Todo task to InProgress. A missing or already claimed task fails.
/// </summary>
public class ClaimTaskHandler : ICommandHandler<ClaimTaskCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ClaimTaskHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(ClaimTaskCommand command, CancellationToken ct = default)
    {
        var projectId = command.Request.ProjectId;
        if (projectId == Guid.Empty)
            return Result.Failure<TaskItemDto>("Task not found or already claimed.");

        await using var db = _dbFactory.CreateDbContext();
        var taskId = command.Request.TaskId;
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Serialize task claiming with ReleasePrepare before locking the task row.
            if (!await ProjectRuntimeMutationGate.TryLockProjectAsync(db, projectId, ct))
                return Result.Failure<TaskItemDto>("Project not found.", ErrorKind.NotFound);

            // Explicit project predicate: isolation must not rest on the global tenant filter alone.
            var task = await db.Tasks
                .FromSqlRaw(
                    "SELECT * FROM \"Tasks\" WHERE \"Id\" = {0} AND \"ProjectId\" = {1} AND \"Status\" = 'Todo' LIMIT 1 FOR UPDATE SKIP LOCKED",
                    taskId, projectId)
                .FirstOrDefaultAsync(ct);

            if (task is null)
            {
                await tx.RollbackAsync(ct);
                return Result.Failure<TaskItemDto>("Task not found or already claimed.");
            }

            var releaseBlock = await ReleaseTaskMutationGuard.GetBlockReasonAsync(
                db, projectId, [task.MilestoneId], ct);
            if (releaseBlock is not null)
            {
                await tx.RollbackAsync(ct);
                return Result.Failure<TaskItemDto>(releaseBlock, ErrorKind.Conflict);
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