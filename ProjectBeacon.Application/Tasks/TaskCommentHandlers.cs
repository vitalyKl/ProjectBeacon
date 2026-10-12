namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Entities.Tasks;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Adds a comment on a task as the authenticated user.
/// </summary>
public class AddCommentHandler : ICommandHandler<AddCommentCommand, Result<TaskCommentDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public AddCommentHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskCommentDto>> HandleAsync(AddCommentCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FindAsync([command.Request.TaskId], ct);
        if (task is null)
            return Result.Failure<TaskCommentDto>("Task not found.");

        var userId = command.Actor.UserId ?? Guid.Empty;
        var comment = Domain.Entities.Tasks.TaskComment.Create(command.Request.Content, command.Request.TaskId, userId);
        db.TaskComments.Add(comment);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(comment));
    }

    private static TaskCommentDto MapToDto(TaskComment comment) =>
        new(comment.Id, comment.Content, comment.UserId, comment.CreatedAt, comment.UpdatedAt);
}
