namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class ListTaskStepsHandler : ICommandHandler<ListTaskStepsCommand, Result<IList<TaskStepDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListTaskStepsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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

public class AddTaskStepHandler : ICommandHandler<AddTaskStepCommand, Result<TaskStepDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public AddTaskStepHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskStepDto>> HandleAsync(AddTaskStepCommand command, CancellationToken ct = default)
    {
        var title = command.Request.Title?.Trim() ?? "";
        if (title.Length == 0 || title.Length > 200)
            return Result.Failure<TaskStepDto>("Title is required (max 200 characters).");

        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
        if (task is null)
            return Result.Failure<TaskStepDto>("Task not found.");

        var next = await db.TaskSteps.Where(s => s.TaskId == task.Id).MaxAsync(s => (int?)s.SortOrder, ct) ?? -1;
        var step = TaskStep.Create(task.Id, task.ProjectId, title, next + 1);
        db.TaskSteps.Add(step);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ListTaskStepsHandler.Map(step));
    }
}

public class ToggleTaskStepHandler : ICommandHandler<ToggleTaskStepCommand, Result<TaskStepDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ToggleTaskStepHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskStepDto>> HandleAsync(ToggleTaskStepCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var step = await db.TaskSteps.FirstOrDefaultAsync(s => s.Id == command.Request.StepId, ct);
        if (step is null)
            return Result.Failure<TaskStepDto>("Step not found.");
        step.SetDone(command.Request.Done);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ListTaskStepsHandler.Map(step));
    }
}

public class DeleteTaskStepHandler : ICommandHandler<DeleteTaskStepCommand, Result>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DeleteTaskStepHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DeleteTaskStepCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var step = await db.TaskSteps.FirstOrDefaultAsync(s => s.Id == command.Request.StepId, ct);
        if (step is null)
            return Result.Failure("Step not found.");
        db.TaskSteps.Remove(step);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
