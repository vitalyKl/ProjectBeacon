namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Loads one task with comments and dependencies.
/// </summary>
public class GetTaskHandler : ICommandHandler<GetTaskCommand, Result<TaskItemDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetTaskHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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
/// <summary>
/// Lists tasks on a project. An optional status filter uses Todo, InProgress, or Done.
/// </summary>
public class ListProjectTasksHandler : ICommandHandler<ListProjectTasksCommand, Result<IList<TaskItemDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListProjectTasksHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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
/// <summary>
/// Lists a project's tasks in one status. An unknown status returns an empty list.
/// </summary>
public class ListTasksByStatusHandler : ICommandHandler<ListTasksByStatusCommand, Result<IList<TaskItemDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListTasksByStatusHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<TaskItemDto>>> HandleAsync(ListTasksByStatusCommand command, CancellationToken ct = default)
    {
        if (!Enum.TryParse<TaskItemStatus>(command.Request.Status, ignoreCase: true, out var status))
            return Result.Ok((IList<TaskItemDto>)new List<TaskItemDto>());

        await using var db = _dbFactory.CreateDbContext();
        var tasks = await db.Tasks
            .Where(t => t.ProjectId == command.Request.ProjectId && t.Status == status)
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
