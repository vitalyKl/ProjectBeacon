namespace ProjectBeacon.Application.Tasks;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Tasks;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Replaces the tasks this task depends on. A task cannot depend on itself.
/// </summary>
public class SetDependenciesHandler : ICommandHandler<SetDependenciesCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public SetDependenciesHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<TaskItemDto>> HandleAsync(SetDependenciesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks
            .Include(t => t.Dependencies)
            .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);

        if (task is null)
            return Result.Failure<TaskItemDto>("Task not found.");

        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Task, AuthAction.Update,
            projectId: task.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<TaskItemDto>(permission);

        var existingIds = task.Dependencies.Select(d => d.DependentTaskId).ToList();
        var toRemove = existingIds.Except(command.Request.DependentTaskIds);
        var toAdd = command.Request.DependentTaskIds.Except(existingIds);

        foreach (var id in toRemove)
        {
            var dep = task.Dependencies.FirstOrDefault(d => d.DependentTaskId == id);
            if (dep is not null)
                db.TaskDependencies.Remove(dep);
        }

        foreach (var id in toAdd)
        {
            if (id == task.Id)
                return Result.Failure<TaskItemDto>("A task cannot depend on itself.");

            var depTaskExists = await db.Tasks.AnyAsync(t => t.Id == id && t.ProjectId == task.ProjectId, ct);
            if (!depTaskExists)
                return Result.Failure<TaskItemDto>($"Dependent task {id} not found.");

            db.TaskDependencies.Add(Domain.Entities.Tasks.TaskDependency.Create(task.Id, id));
        }

        await db.SaveChangesAsync(ct);
        await db.Entry(task).Collection(t => t.Dependencies).LoadAsync(ct);

        return Result.Ok(MapToDto(task));
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
            task.Dependencies.Select(d => d.DependentTaskId).ToList());
}
