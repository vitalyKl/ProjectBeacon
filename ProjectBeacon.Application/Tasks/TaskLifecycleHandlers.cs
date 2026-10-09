namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Creates a task. Label and milestone, when set, must belong to the project.
/// </summary>
public class CreateTaskHandler : ICommandHandler<CreateTaskCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public CreateTaskHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(CreateTaskCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var projectExists = await db.Projects.AnyAsync(p => p.Id == command.Request.ProjectId, ct);
        if (!projectExists)
            return Result.Failure<TaskItemDto>("Project not found.");

        Guid? labelId = command.Request.LabelId;
        if (labelId is not null)
        {
            var labelExists = await db.Labels.AnyAsync(l => l.Id == labelId && l.ProjectId == command.Request.ProjectId, ct);
            if (!labelExists)
                return Result.Failure<TaskItemDto>("Label not found.");
        }
        else if (!string.IsNullOrWhiteSpace(command.Request.Path))
        {
            var labels = await db.Labels
                .Include(l => l.Paths)
                .Where(l => l.ProjectId == command.Request.ProjectId)
                .ToListAsync(ct);
            labelId = AutoLabel.Match(labels, command.Request.Path)?.Id;
        }

        if (command.Request.MilestoneId is not null)
        {
            var milestoneExists = await db.Milestones.AnyAsync(m => m.Id == command.Request.MilestoneId && m.ProjectId == command.Request.ProjectId, ct);
            if (!milestoneExists)
                return Result.Failure<TaskItemDto>("Milestone not found.");
        }

        var task = Domain.Entities.Projects.TaskItem.Create(command.Request.Title, command.Request.ProjectId, command.Request.Priority, command.Request.Type);
        task.Update(description: command.Request.Description);
        task.AssignLabel(labelId);
        task.SetMilestone(command.Request.MilestoneId);

        db.Tasks.Add(task);
        if (command.Actor.UserId is { } actorId && actorId != Guid.Empty)
            await TaskKindCopy.CopyOntoTaskAsync(db, task, actorId, command.Request.KindId, ct);
        await db.SaveChangesAsync(ct);

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
            []);
}
/// <summary>
/// Updates a task title, description, priority, type, label, or milestone.
/// </summary>
public class UpdateTaskHandler : ICommandHandler<UpdateTaskCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public UpdateTaskHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(UpdateTaskCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks
            .Include(t => t.Project)
            .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);

        if (task is null)
            return Result.Failure<TaskItemDto>("Task not found.");

        if (command.Request.LabelId is not null)
        {
            var labelExists = await db.Labels.AnyAsync(l => l.Id == command.Request.LabelId && l.ProjectId == task.ProjectId, ct);
            if (!labelExists)
                return Result.Failure<TaskItemDto>("Label not found.");
        }

        if (command.Request.MilestoneId is not null)
        {
            var milestoneExists = await db.Milestones.AnyAsync(m => m.Id == command.Request.MilestoneId && m.ProjectId == task.ProjectId, ct);
            if (!milestoneExists)
                return Result.Failure<TaskItemDto>("Milestone not found.");
        }

        task.Update(command.Request.Title, command.Request.Description, command.Request.Priority, command.Request.Type);
        task.AssignLabel(command.Request.LabelId);
        task.SetMilestone(command.Request.MilestoneId);

        await db.SaveChangesAsync(ct);

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
            []);
}
/// <summary>
/// Deletes a task.
/// </summary>
public class DeleteTaskHandler : ICommandHandler<DeleteTaskCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public DeleteTaskHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DeleteTaskCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FindAsync([command.Request.TaskId], ct);
        if (task is null)
            return Result.Failure("Task not found.");

        db.Tasks.Remove(task);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
/// <summary>
/// Sets the in-progress sub-stage. The domain rejects the change unless the task is InProgress.
/// </summary>
public class ChangeSubStageHandler : ICommandHandler<ChangeSubStageCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ChangeSubStageHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(ChangeSubStageCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FindAsync([command.Request.TaskId], ct);
        if (task is null)
            return Result.Failure<TaskItemDto>("Task not found.");

        try
        {
            task.MoveToSubStage(command.Request.SubStage);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<TaskItemDto>(ex.Message);
        }

        await db.SaveChangesAsync(ct);

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
            []);
}
/// <summary>
/// Saves review notes. The domain rejects an empty note.
/// </summary>
public class AddReviewNotesHandler : ICommandHandler<AddReviewNotesCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public AddReviewNotesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(AddReviewNotesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FindAsync([command.Request.TaskId], ct);
        if (task is null)
            return Result.Failure<TaskItemDto>("Task not found.");

        try
        {
            task.SetReviewNotes(command.Request.ReviewNotes);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<TaskItemDto>(ex.Message);
        }

        await db.SaveChangesAsync(ct);

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
            []);
}
