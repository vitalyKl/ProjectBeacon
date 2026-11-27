namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Application.Releases;
using Domain.Entities.Tasks;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Lists checklist steps on a task.
/// </summary>
public class ListTaskStepsHandler : ICommandHandler<ListTaskStepsCommand, Result<IList<TaskStepDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListTaskStepsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<TaskStepDto>>> HandleAsync(ListTaskStepsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<IList<TaskStepDto>>("Task not found.");
        var steps = await db.TaskSteps
            .Where(s => s.TaskId == task.Id)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return Result.Ok((IList<TaskStepDto>)steps.Select(Map).ToList());
    }

    internal static TaskStepDto Map(TaskStep step) =>
        new(step.Id, step.TaskId, step.Title, step.SortOrder, step.DoneAt, step.IsDone);
}
/// <summary>
/// Adds a checklist step. Title is required and at most 200 characters.
/// </summary>
public class AddTaskStepHandler : ICommandHandler<AddTaskStepCommand, Result<TaskStepDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public AddTaskStepHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskStepDto>> HandleAsync(AddTaskStepCommand command, CancellationToken ct = default)
    {
        var title = command.Request.Title?.Trim() ?? "";
        if (title.Length == 0 || title.Length > 200)
            return Result.Failure<TaskStepDto>("Title is required (max 200 characters).");

        return await ReleaseScopeMutationCoordinator.ExecuteForTaskAsync(
            _dbFactory,
            command.Request.TaskId,
            null,
            async (db, task, token) =>
            {
                var next = await db.TaskSteps
                    .Where(step => step.TaskId == task.Id)
                    .MaxAsync(step => (int?)step.SortOrder, token) ?? -1;
                var step = TaskStep.Create(task.Id, task.ProjectId, title, next + 1);
                db.TaskSteps.Add(step);
                await db.SaveChangesAsync(token);
                return Result.Ok(ListTaskStepsHandler.Map(step));
            },
            ct);
    }
}
/// <summary>
/// Marks a step done or not done.
/// </summary>
public class ToggleTaskStepHandler : ICommandHandler<ToggleTaskStepCommand, Result<TaskStepDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ToggleTaskStepHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskStepDto>> HandleAsync(ToggleTaskStepCommand command, CancellationToken ct = default)
    {
        return await ReleaseScopeMutationCoordinator.ExecuteForStepAsync(
            _dbFactory,
            command.Request.StepId,
            async (db, task, step, token) =>
            {
                step.SetDone(command.Request.Done);
                await db.SaveChangesAsync(token);
                return Result.Ok(ListTaskStepsHandler.Map(step));
            },
            ct);
    }
}
/// <summary>
/// Deletes a checklist step.
/// </summary>
public class DeleteTaskStepHandler : ICommandHandler<DeleteTaskStepCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public DeleteTaskStepHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DeleteTaskStepCommand command, CancellationToken ct = default)
    {
        var result = await ReleaseScopeMutationCoordinator.ExecuteForStepAsync(
            _dbFactory,
            command.Request.StepId,
            async (db, task, step, token) =>
            {
                db.TaskSteps.Remove(step);
                await db.SaveChangesAsync(token);
                return Result.Ok(true);
            },
            ct);
        return result.Success
            ? Result.Ok()
            : Result.Failure(result.Error ?? "Checklist step delete conflict.", result.Kind);
    }
}

