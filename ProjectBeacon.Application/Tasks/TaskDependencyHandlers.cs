namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Application.Releases;
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

    public SetDependenciesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(SetDependenciesCommand command, CancellationToken ct = default)
    {
        return await ReleaseScopeMutationCoordinator.ExecuteForTaskAsync(
            _dbFactory,
            command.Request.TaskId,
            null,
            async (db, task, token) =>
            {
                await db.Entry(task).Collection(item => item.Dependencies).LoadAsync(token);
                var existingIds = task.Dependencies.Select(dependency => dependency.DependentTaskId).ToList();
                var toRemove = existingIds.Except(command.Request.DependentTaskIds);
                var toAdd = command.Request.DependentTaskIds.Except(existingIds);

                foreach (var id in toRemove)
                {
                    var dependency = task.Dependencies.FirstOrDefault(item => item.DependentTaskId == id);
                    if (dependency is not null)
                        db.TaskDependencies.Remove(dependency);
                }

                foreach (var id in toAdd)
                {
                    if (id == task.Id)
                        return Result.Failure<TaskItemDto>("A task cannot depend on itself.");

                    var dependentTaskExists = await db.Tasks.AnyAsync(
                        item => item.Id == id && item.ProjectId == task.ProjectId, token);
                    if (!dependentTaskExists)
                        return Result.Failure<TaskItemDto>($"Dependent task {id} not found.");

                    db.TaskDependencies.Add(Domain.Entities.Tasks.TaskDependency.Create(task.Id, id));
                }

                await db.SaveChangesAsync(token);
                await db.Entry(task).Collection(item => item.Dependencies).LoadAsync(token);
                return Result.Ok(MapToDto(task));
            },
            ct);
    }

    private static TaskItemDto MapToDto(TaskItem task) =>
        new(
            task.Id, task.Title, task.Description, task.Status.ToString(), task.Priority, task.Type,
            task.Status == TaskItemStatus.InProgress ? task.SubStage : null,
            task.ProjectId, task.LabelId, task.MilestoneId, task.ReviewNotes,
            task.CreatedAt, task.CompletedAt, [],
            task.Dependencies.Select(dependency => dependency.DependentTaskId).ToList(),
            task.ContentRevision, task.HotfixSourceReleaseId, task.HotfixDeployedSha,
            task.HotfixTargetBranch, task.HotfixRequestId, task.ForwardPortOfTaskId);
}

