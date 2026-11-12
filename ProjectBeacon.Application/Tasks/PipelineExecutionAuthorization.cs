namespace ProjectBeacon.Application.Tasks;

using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Re-checks project execution permission and the server-pinned role/session scope
/// for short-lived pipeline harness actors at the Application boundary.
/// Manual project tokens have no pipeline role claims and are authorized by SessionDrive
/// plus their explicit project binding.
/// </summary>
internal static class PipelineExecutionAuthorization
{
    public static async Task<Result> CanDriveAsync(
        IBeaconDb db,
        IAuthorizationService authorization,
        ActorContext? actor,
        TaskItem task,
        PipelineRole requiredHarnessRole,
        Guid? requiredSubtaskId,
        CancellationToken ct)
    {
        if (actor is null || (!actor.IsHuman && !actor.IsApiToken))
            return Result.Forbidden();

        var permission = await authorization.CanAsync(
            db, actor, ResourceType.Pipeline, AuthAction.Execute,
            projectId: task.ProjectId, ct: ct);
        if (!permission.Success)
            return permission;

        // Human actors and manual project tokens use normal project authorization.
        // Harness tokens are server-pinned and must match the exact task/session/role.
        if (!actor.IsApiToken || actor.PipelineRole is null)
            return Result.Ok();

        if (actor.PipelineRole != requiredHarnessRole
            || actor.PipelineTaskId != task.Id
            || actor.PipelineSubtaskId != requiredSubtaskId
            || actor.PipelineSessionId is not { } sessionId)
            return Result.Forbidden();

        var activeSession = await db.PipelineSessions.AsNoTracking().AnyAsync(session =>
            session.Id == sessionId
            && session.TaskId == task.Id
            && session.ProjectId == task.ProjectId
            && session.Role == requiredHarnessRole
            && session.SubtaskId == requiredSubtaskId
            && session.Status == SessionStatus.Active, ct);
        return activeSession ? Result.Ok() : Result.Forbidden();
    }
}
