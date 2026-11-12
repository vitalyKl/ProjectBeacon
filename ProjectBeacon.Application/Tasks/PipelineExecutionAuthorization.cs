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
        ActorContext actor,
        TaskItem task,
        PipelineRole requiredHarnessRole,
        Guid? requiredSubtaskId,
        CancellationToken ct)
    {
        if (!actor.IsHuman && !actor.IsApiToken)
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

    /// <summary>
    /// Explicitly scopes trusted in-process queue dispatch to one project and task.
    /// Worker actors are never created from HTTP principals or accepted as general
    /// project actors by the authorization service.
    /// </summary>
    public static Result CanDispatchInternally(ActorContext actor, TaskItem task) =>
        actor.IsWorker
        && actor.ProjectId == task.ProjectId
        && actor.PipelineTaskId == task.Id
            ? Result.Ok()
            : Result.Forbidden();

    /// <summary>
    /// Authorizes a launch initiated by a human, a project-scoped SessionDrive token,
    /// the active Planner harness, or the internal task-scoped queue dispatcher.
    /// </summary>
    public static async Task<Result> CanLaunchAsync(
        IBeaconDb db,
        IAuthorizationService authorization,
        ActorContext actor,
        TaskItem task,
        PipelineSession session,
        CancellationToken ct)
    {
        if (session.TaskId != task.Id || session.ProjectId != task.ProjectId)
            return Result.Forbidden();

        if (actor.IsWorker)
            return CanDispatchInternally(actor, task);

        // The active Planner may launch Actor work or the Review session it just created.
        // Actor and Reviewer harness tokens cannot launch other sessions.
        if (actor.IsApiToken && actor.PipelineRole is not null)
        {
            if (actor.PipelineRole != PipelineRole.Planner
                || session.Role is not (PipelineRole.Actor or PipelineRole.Review))
                return Result.Forbidden();

            return await CanDriveAsync(
                db, authorization, actor, task, PipelineRole.Planner,
                requiredSubtaskId: null, ct);
        }

        return await CanManageLifecycleAsync(db, authorization, actor, task, ct);
    }

    /// <summary>
    /// Authorizes manual lifecycle operations initiated by a human or a project-scoped
    /// SessionDrive token. Short-lived harness tokens cannot manually retry sessions.
    /// </summary>
    public static async Task<Result> CanManageLifecycleAsync(
        IBeaconDb db,
        IAuthorizationService authorization,
        ActorContext actor,
        TaskItem task,
        CancellationToken ct)
    {
        if (!actor.IsHuman && !actor.IsApiToken)
            return Result.Forbidden();

        if (actor.IsApiToken && actor.PipelineRole is not null)
            return Result.Forbidden();

        return await authorization.CanAsync(
            db, actor, ResourceType.Pipeline, AuthAction.Execute,
            projectId: task.ProjectId, ct: ct);
    }
}
