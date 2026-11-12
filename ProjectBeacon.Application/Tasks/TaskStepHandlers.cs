namespace ProjectBeacon.Application.Tasks;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Tasks;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Lists checklist steps when the actor can read the parent task.</summary>
public class ListTaskStepsHandler : ICommandHandler<ListTaskStepsCommand, Result<IList<TaskStepDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ListTaskStepsHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<IList<TaskStepDto>>> HandleAsync(ListTaskStepsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<IList<TaskStepDto>>("Task not found.");

        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.TaskStep, AuthAction.Read,
            projectId: task.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<IList<TaskStepDto>>(permission);

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

/// <summary>Adds a checklist step to a task the actor can update.</summary>
public class AddTaskStepHandler : ICommandHandler<AddTaskStepCommand, Result<TaskStepDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public AddTaskStepHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<TaskStepDto>> HandleAsync(AddTaskStepCommand command, CancellationToken ct = default)
    {
        var title = command.Request.Title?.Trim() ?? "";
        if (title.Length == 0 || title.Length > 200)
            return Result.Failure<TaskStepDto>("Title is required (max 200 characters).");

        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<TaskStepDto>("Task not found.");

        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.TaskStep, AuthAction.Create,
            projectId: task.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<TaskStepDto>(permission);

        var next = await db.TaskSteps.Where(s => s.TaskId == task.Id).MaxAsync(s => (int?)s.SortOrder, ct) ?? -1;
        var step = TaskStep.Create(task.Id, task.ProjectId, title, next + 1);
        db.TaskSteps.Add(step);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ListTaskStepsHandler.Map(step));
    }
}

/// <summary>Marks a step done or not done when the actor can update its parent task.</summary>
public class ToggleTaskStepHandler : ICommandHandler<ToggleTaskStepCommand, Result<TaskStepDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ToggleTaskStepHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<TaskStepDto>> HandleAsync(ToggleTaskStepCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var step = await db.TaskSteps.FirstOrDefaultAsync(s => s.Id == command.Request.StepId, ct);
        if (step is null)
            return Result.Failure<TaskStepDto>("Step not found.");

        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.TaskStep, AuthAction.Update,
            projectId: step.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<TaskStepDto>(permission);

        step.SetDone(command.Request.Done);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ListTaskStepsHandler.Map(step));
    }
}

/// <summary>Deletes a checklist step; project Owner/Admin is required.</summary>
public class DeleteTaskStepHandler : ICommandHandler<DeleteTaskStepCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public DeleteTaskStepHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result> HandleAsync(DeleteTaskStepCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var step = await db.TaskSteps.FirstOrDefaultAsync(s => s.Id == command.Request.StepId, ct);
        if (step is null)
            return Result.Failure("Step not found.");

        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.TaskStep, AuthAction.Delete,
            projectId: step.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure(permission);

        db.TaskSteps.Remove(step);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
