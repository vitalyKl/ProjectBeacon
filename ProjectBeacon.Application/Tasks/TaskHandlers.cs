namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class CreateTaskHandler : ICommandHandler<CreateTaskCommand, Result<TaskItemDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CreateTaskHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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

public class UpdateTaskHandler : ICommandHandler<UpdateTaskCommand, Result<TaskItemDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public UpdateTaskHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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

public class DeleteTaskHandler : ICommandHandler<DeleteTaskCommand, Result<bool>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DeleteTaskHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<bool>> HandleAsync(DeleteTaskCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FindAsync([command.Request.TaskId], ct);
        if (task is null)
            return Result.Failure<bool>("Task not found.");

        db.Tasks.Remove(task);
        await db.SaveChangesAsync(ct);

        return Result.Ok(true);
    }
}

public class ChangeSubStageHandler : ICommandHandler<ChangeSubStageCommand, Result<TaskItemDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ChangeSubStageHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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

public class AddCommentHandler : ICommandHandler<AddCommentCommand, Result<TaskCommentDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public AddCommentHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskCommentDto>> HandleAsync(AddCommentCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FindAsync([command.Request.TaskId], ct);
        if (task is null)
            return Result.Failure<TaskCommentDto>("Task not found.");

        var userId = command.Request.UserId == Guid.Empty ? Guid.Empty : command.Request.UserId;
        var comment = Domain.Entities.Projects.TaskComment.Create(command.Request.Content, command.Request.TaskId, userId);
        db.TaskComments.Add(comment);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(comment));
    }

    private static TaskCommentDto MapToDto(TaskComment comment) =>
        new(comment.Id, comment.Content, comment.UserId, comment.CreatedAt, comment.UpdatedAt);
}

public class SetDependenciesHandler : ICommandHandler<SetDependenciesCommand, Result<TaskItemDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public SetDependenciesHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(SetDependenciesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks
            .Include(t => t.Dependencies)
            .FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);

        if (task is null)
            return Result.Failure<TaskItemDto>("Task not found.");

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

            db.TaskDependencies.Add(Domain.Entities.Projects.TaskDependency.Create(task.Id, id));
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

public class AddReviewNotesHandler : ICommandHandler<AddReviewNotesCommand, Result<TaskItemDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public AddReviewNotesHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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

public class GetTaskHandler : ICommandHandler<GetTaskCommand, Result<TaskItemDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetTaskHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskItemDto>> HandleAsync(GetTaskCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var query = db.Tasks
            .Include(t => t.Comments)
            .Include(t => t.Dependencies)
            .AsQueryable();
        if (command.Request.ProjectId is Guid projectId)
            query = query.Where(t => t.ProjectId == projectId);

        var task = await query.FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);

        if (task is null)
            return Result.Failure<TaskItemDto>("Task not found.");

        return Result.Ok(new TaskItemDto(
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
            task.Comments.Select(c => new TaskCommentDto(c.Id, c.Content, c.UserId, c.CreatedAt, c.UpdatedAt)).ToList(),
            task.Dependencies.Select(d => d.DependentTaskId).ToList()));
    }
}

public class ListProjectTasksHandler : ICommandHandler<ListProjectTasksCommand, Result<IList<TaskItemDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListProjectTasksHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<TaskItemDto>>> HandleAsync(ListProjectTasksCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var tasks = await db.Tasks
            .Where(t => t.ProjectId == command.Request.ProjectId)
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

        var result = tasks.Select(t => new TaskItemDto(
            t.Id, t.Title, t.Description, t.Status.ToString(),
            t.Priority, t.Type,
            t.Status == TaskItemStatus.InProgress ? t.SubStage : null,
            t.ProjectId, t.LabelId, t.MilestoneId, t.ReviewNotes,
            t.CreatedAt, t.CompletedAt, [], []));

        return Result.Ok((IList<TaskItemDto>)result.ToList());
    }
}

public class ListTasksByStatusHandler : ICommandHandler<ListTasksByStatusCommand, Result<IList<TaskItemDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListTasksByStatusHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<TaskItemDto>>> HandleAsync(ListTasksByStatusCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var tasks = await db.Tasks
            .Where(t => t.ProjectId == command.Request.ProjectId && t.Status.ToString().Equals(command.Request.Status, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

        var result = tasks.Select(t => new TaskItemDto(
            t.Id, t.Title, t.Description, t.Status.ToString(),
            t.Priority, t.Type,
            t.Status == TaskItemStatus.InProgress ? t.SubStage : null,
            t.ProjectId, t.LabelId, t.MilestoneId, t.ReviewNotes,
            t.CreatedAt, t.CompletedAt, [], []));

        return Result.Ok((IList<TaskItemDto>)result.ToList());
    }
}