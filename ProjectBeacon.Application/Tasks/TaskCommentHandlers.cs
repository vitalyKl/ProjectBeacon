namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Application.Authorization;
using Domain.Entities.Tasks;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Adds a comment on a task as the authenticated user.
/// </summary>
public class AddCommentHandler : ICommandHandler<AddCommentCommand, Result<TaskCommentDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public AddCommentHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<TaskCommentDto>> HandleAsync(AddCommentCommand command, CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman || command.Actor.UserId is not { } userId || userId == Guid.Empty)
            return Result.Failure<TaskCommentDto>("A human project member must author task comments.", ErrorKind.Forbidden);

        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FindAsync([command.Request.TaskId], ct);
        if (task is null)
            return Result.Failure<TaskCommentDto>("Task not found.");

        var permission = await _authorization.CanAsync(
            db,
            command.Actor,
            ResourceType.Task,
            AuthAction.Create,
            projectId: task.ProjectId,
            ct: ct);
        if (!permission.Success)
            return Result.Failure<TaskCommentDto>(permission);

        var comment = Domain.Entities.Tasks.TaskComment.Create(command.Request.Content, command.Request.TaskId, userId);
        db.TaskComments.Add(comment);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(comment));
    }

    private static TaskCommentDto MapToDto(TaskComment comment) =>
        new(comment.Id, comment.Content, comment.UserId, comment.CreatedAt, comment.UpdatedAt);
}
