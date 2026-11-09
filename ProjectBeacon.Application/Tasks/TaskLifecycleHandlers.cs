namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Creates a task. Label and milestone, when set, must belong to the project.
/// </summary>
public class CreateTaskHandler : ICommandHandler<CreateTaskCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public CreateTaskHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public CreateTaskHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

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

        var task = Domain.Entities.Tasks.TaskItem.Create(
            command.Request.Title,
            command.Request.ProjectId,
            command.Request.Priority,
            command.Request.Type,
            command.Request.Description);
        task.AssignLabel(labelId);
        task.SetMilestone(command.Request.MilestoneId);

        db.Tasks.Add(task);
        if (command.Actor.UserId is { } actorId && actorId != Guid.Empty)
            await TaskKindCopy.CopyOntoTaskAsync(db, task, actorId, command.Request.KindId, ct);
        var attribution = ContentAttribution.FromActor(command.Actor);
        var saved = await _contentWriter.SaveAsync(
            db, task, attribution.Type ?? "System", attribution.Id, ct);
        if (!saved.Success)
            return Result.Failure<TaskItemDto>(saved.Error ?? "Task content revision conflict.", ErrorKind.Conflict);

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
            [],
            task.ContentRevision);
}
/// <summary>
/// Updates a task title, description, priority, type, label, or milestone. Description edits require the caller's expected content revision; state transitions use the separate StateRevision.
/// </summary>
public class UpdateTaskHandler : ICommandHandler<UpdateTaskCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public UpdateTaskHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public UpdateTaskHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

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

        var contentChanged = command.Request.Description is not null
            && !string.Equals(task.Description, command.Request.Description, StringComparison.Ordinal);
        if (contentChanged && command.Request.ExpectedContentRevision != task.ContentRevision)
            return Result.Failure<TaskItemDto>(
                "Task content revision conflict. Reload the task before saving.",
                ErrorKind.Conflict);

        task.Update(command.Request.Title, command.Request.Description, command.Request.Priority, command.Request.Type);
        task.AssignLabel(command.Request.LabelId);
        task.SetMilestone(command.Request.MilestoneId);

        if (contentChanged)
        {
            var attribution = ContentAttribution.FromActor(command.Actor);
            var saved = await _contentWriter.SaveAsync(
                db, task, attribution.Type ?? "System", attribution.Id, ct);
            if (!saved.Success)
                return Result.Failure<TaskItemDto>(saved.Error ?? "Task content revision conflict.", ErrorKind.Conflict);
        }
        else
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<TaskItemDto>(
                    "Task changed while saving. Reload before retrying.",
                    ErrorKind.Conflict);
            }
        }

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
            [],
            task.ContentRevision);
}
/// <summary>
/// Deletes a task.
/// </summary>
public class DeleteTaskHandler : ICommandHandler<DeleteTaskCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public DeleteTaskHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public DeleteTaskHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result> HandleAsync(DeleteTaskCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == command.Request.TaskId, ct);
            if (task is null)
                return Result.Failure("Task not found.");

            var sessions = await db.PipelineSessions.IgnoreQueryFilters()
                .Where(session => session.ProjectId == task.ProjectId && session.TaskId == task.Id)
                .ToListAsync(ct);
            foreach (var session in sessions.Where(session =>
                session.Status is SessionStatus.Ready or SessionStatus.Active or SessionStatus.WaitingForUser))
                session.Close();

            await db.SaveChangesAsync(ct);
            await PipelineSupport.CancelPipelineCommandsAsync(db, task.ProjectId, sessions.Select(session => session.Id).ToArray(), ct);
            await PipelineSupport.CancelReviewCheckCommandsAsync(db, task.ProjectId, task.Id, ct);

            // These records are indexed by task but intentionally have no cascade FK.
            var phases = await db.TaskPhases.IgnoreQueryFilters()
                .Where(phase => phase.ProjectId == task.ProjectId && phase.TaskId == task.Id).ToListAsync(ct);
            var taskSteps = await db.TaskSteps.IgnoreQueryFilters()
                .Where(step => step.ProjectId == task.ProjectId && step.TaskId == task.Id).ToListAsync(ct);
            var reviewRuns = await db.ReviewRuns.IgnoreQueryFilters()
                .Where(run => run.ProjectId == task.ProjectId && run.TaskId == task.Id).ToListAsync(ct);
            var evalRuns = await db.EvalRuns.IgnoreQueryFilters()
                .Where(run => run.ProjectId == task.ProjectId && run.TaskId == task.Id).ToListAsync(ct);
            var dependencies = await db.TaskDependencies.IgnoreQueryFilters()
                .Where(dependency => dependency.TaskId == task.Id || dependency.DependentTaskId == task.Id)
                .ToListAsync(ct);

            db.TaskPhases.RemoveRange(phases);
            db.TaskSteps.RemoveRange(taskSteps);
            db.ReviewRuns.RemoveRange(reviewRuns);
            db.EvalRuns.RemoveRange(evalRuns);
            db.TaskDependencies.RemoveRange(dependencies);
            task.MarkContentDeleted();
            var attribution = ContentAttribution.FromActor(command.Actor);
            _contentWriter.AddVersion(
                db, task, attribution.Type ?? "System", attribution.Id, deleted: true);
            db.Tasks.Remove(task);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Ok();
        });
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

        // Unguarded by design: this writer never touches StateRevision, so it cannot
        // create a second finish-work winner. Under a race with finish_work it may leave
        // a stale sub-stage on a Done task; the DTO hides sub-stage unless InProgress.
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
            [],
            task.ContentRevision);
}
/// <summary>
/// Saves review notes. The domain rejects an empty note. Last-writer-wins by design: the notes are free-form review text; only their non-emptiness is read at a status transition.
/// </summary>
public class AddReviewNotesHandler : ICommandHandler<AddReviewNotesCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public AddReviewNotesHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public AddReviewNotesHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<TaskItemDto>> HandleAsync(AddReviewNotesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FindAsync([command.Request.TaskId], ct);
        if (task is null)
            return Result.Failure<TaskItemDto>("Task not found.");

        var contentChanged = !string.Equals(task.ReviewNotes, command.Request.ReviewNotes, StringComparison.Ordinal);
        if (contentChanged && command.Request.ExpectedContentRevision != task.ContentRevision)
            return Result.Failure<TaskItemDto>(
                "Task content revision conflict. Reload before saving review notes.",
                ErrorKind.Conflict);

        try
        {
            task.SetReviewNotes(command.Request.ReviewNotes);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<TaskItemDto>(ex.Message);
        }

        if (contentChanged)
        {
            var attribution = ContentAttribution.FromActor(command.Actor);
            var saved = await _contentWriter.SaveAsync(
                db, task, attribution.Type ?? "System", attribution.Id, ct);
            if (!saved.Success)
                return Result.Failure<TaskItemDto>(saved.Error ?? "Task review note revision conflict.", ErrorKind.Conflict);
        }
        else
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<TaskItemDto>(
                    "Task changed while saving. Reload before retrying.",
                    ErrorKind.Conflict);
            }
        }

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
            [],
            task.ContentRevision);
}
