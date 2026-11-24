namespace ProjectBeacon.Application.Tasks;

using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Read model for an agent-declared version impact attached to a task.</summary>
public sealed record TaskVersionImpactDto(
    Guid TaskId,
    Guid ProjectId,
    string? Impact,
    string? Rationale,
    DateTime? DeclaredAt,
    int Revision);

public sealed record SetTaskVersionImpactCommand(
    Guid TaskId,
    string Impact,
    string Rationale,
    int ExpectedRevision,
    ActorContext Actor);

public sealed class GetTaskVersionImpactHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetTaskVersionImpactHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskVersionImpactDto>> HandleAsync(
        Guid taskId, ActorContext actor, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(item => item.Id == taskId, ct);
        if (task is null)
            return Result.Failure<TaskVersionImpactDto>("Task not found.", ErrorKind.NotFound);
        if (!await CanAccessProjectAsync(db, task.ProjectId, actor, readOnly: true, ct))
            return Result.Forbidden<TaskVersionImpactDto>();
        return Result.Ok(Map(task));
    }

    internal static TaskVersionImpactDto Map(TaskItem task) =>
        new(task.Id, task.ProjectId, task.VersionImpact, task.VersionImpactRationale,
            task.VersionImpactAt, task.VersionImpactRevision);

    internal static async Task<bool> CanAccessProjectAsync(
        IBeaconDb db, Guid projectId, ActorContext actor, bool readOnly, CancellationToken ct)
    {
        if (actor.IsAdmin)
            return true;
        if (actor.IsApiToken)
        {
            var required = readOnly ? ApiTokenCapability.TaskRead : ApiTokenCapability.TaskWrite;
            return actor.Capabilities.HasFlag(ApiTokenCapability.Admin)
                || actor.Capabilities.HasFlag(required)
                    ? actor.ProjectId is null || actor.ProjectId == projectId
                    : false;
        }
        if (!actor.IsHuman || actor.UserId is null)
            return false;
        var projectMember = await db.ProjectMembers.AnyAsync(
            member => member.ProjectId == projectId && member.UserId == actor.UserId, ct);
        if (projectMember)
            return true;
        var orgId = await db.Projects
            .Where(project => project.Id == projectId)
            .Select(project => (Guid?)project.OrgId)
            .FirstOrDefaultAsync(ct);
        return orgId is not null && await db.OrgMembers.AnyAsync(
            member => member.OrgId == orgId && member.UserId == actor.UserId, ct);
    }
}

public sealed class SetTaskVersionImpactHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public SetTaskVersionImpactHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<TaskVersionImpactDto>> HandleAsync(
        SetTaskVersionImpactCommand command, CancellationToken ct = default)
    {
        if (command.Impact is not ("none" or "patch" or "minor" or "major"))
            return Result.Failure<TaskVersionImpactDto>(
                "Impact must be none, patch, minor, or major.", ErrorKind.Validation);
        if (string.IsNullOrWhiteSpace(command.Rationale) || command.Rationale.Trim().Length > 1000)
            return Result.Failure<TaskVersionImpactDto>(
                "A rationale of 1-1000 characters is required.", ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var task = await db.Tasks.FirstOrDefaultAsync(item => item.Id == command.TaskId, ct);
        if (task is null)
            return Result.Failure<TaskVersionImpactDto>("Task not found.", ErrorKind.NotFound);
        if (!await GetTaskVersionImpactHandler.CanAccessProjectAsync(
            db, task.ProjectId, command.Actor, readOnly: false, ct))
            return Result.Forbidden<TaskVersionImpactDto>();
        if (command.ExpectedRevision < 1 || command.ExpectedRevision != task.VersionImpactRevision)
            return Result.Failure<TaskVersionImpactDto>(
                "Version impact changed since it was loaded. Read it again and retry.", ErrorKind.Conflict);

        try
        {
            task.SetVersionImpact(command.Impact, command.Rationale);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<TaskVersionImpactDto>(
                "Version impact changed concurrently. Read it again and retry.", ErrorKind.Conflict);
        }
        return Result.Ok(GetTaskVersionImpactHandler.Map(task));
    }
}
