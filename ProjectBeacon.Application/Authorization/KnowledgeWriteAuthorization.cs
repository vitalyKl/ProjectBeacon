namespace ProjectBeacon.Application.Authorization;

using Application.Persistence;
using Application.Tasks;
using Domain.Common;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Allows project members to propose knowledge and permits only an active, task-pinned
/// Actor harness to submit agent proposals. It never grants project-wide token write access.
/// </summary>
internal static class KnowledgeWriteAuthorization
{
    public static async Task<Result> CanWriteAsync(
        IBeaconDb db,
        IAuthorizationService authorization,
        ActorContext actor,
        ResourceType resource,
        AuthAction action,
        Guid projectId,
        CancellationToken ct)
    {
        if (actor.IsHuman)
            return await authorization.CanAsync(db, actor, resource, action, projectId: projectId, ct: ct);

        if (!actor.IsApiToken || actor.PipelineRole != PipelineRole.Actor
            || actor.PipelineTaskId is not { } taskId
            || actor.PipelineSubtaskId is not { } subtaskId)
            return Result.Forbidden();

        var task = await db.Tasks.FirstOrDefaultAsync(
            item => item.Id == taskId && item.ProjectId == projectId, ct);
        if (task is null)
            return Result.Forbidden();

        return await PipelineExecutionAuthorization.CanDriveAsync(
            db, authorization, actor, task, PipelineRole.Actor, subtaskId, ct);
    }
}
