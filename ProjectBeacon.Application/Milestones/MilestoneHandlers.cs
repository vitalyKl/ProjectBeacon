namespace ProjectBeacon.Application.Milestones;

using Application.Common;
using Application.Authorization;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Creates a milestone on a project.
/// </summary>
public class CreateMilestoneHandler : ICommandHandler<CreateMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public CreateMilestoneHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<MilestoneDto>> HandleAsync(CreateMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Milestone, AuthAction.Create,
            projectId: command.Request.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<MilestoneDto>(permission);

        var projectExists = await db.Projects.AnyAsync(p => p.Id == command.Request.ProjectId, ct);
        if (!projectExists)
            return Result.Failure<MilestoneDto>("Project not found.");

        var milestone = Domain.Entities.Roadmap.Milestone.Create(
            command.Request.Name,
            command.Request.Description,
            command.Request.ProjectId,
            command.Request.Order);

        db.Milestones.Add(milestone);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt);
}
/// <summary>
/// Updates a milestone name, description, or order.
/// </summary>
public class UpdateMilestoneHandler : ICommandHandler<UpdateMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public UpdateMilestoneHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<MilestoneDto>> HandleAsync(UpdateMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Milestone, AuthAction.Update,
            projectId: milestone.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<MilestoneDto>(permission);

        milestone.Update(command.Request.Name, command.Request.Description, command.Request.Order);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt);
}
/// <summary>
/// Deletes a milestone.
/// </summary>
public class DeleteMilestoneHandler : ICommandHandler<DeleteMilestoneCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public DeleteMilestoneHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result> HandleAsync(DeleteMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure("Milestone not found.");
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Milestone, AuthAction.Delete,
            projectId: milestone.ProjectId, ct: ct);
        if (!permission.Success)
            return permission;

        db.Milestones.Remove(milestone);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
/// <summary>
/// Loads one milestone.
/// </summary>
public class GetMilestoneHandler : ICommandHandler<GetMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public GetMilestoneHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<MilestoneDto>> HandleAsync(GetMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Milestone, AuthAction.Read,
            projectId: milestone.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<MilestoneDto>(permission);

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt);
}
/// <summary>
/// Lists milestones for a project.
/// </summary>
public class ListProjectMilestonesHandler : ICommandHandler<ListProjectMilestonesCommand, Result<IList<MilestoneDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ListProjectMilestonesHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<IList<MilestoneDto>>> HandleAsync(ListProjectMilestonesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Milestone, AuthAction.Read,
            projectId: command.Request.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<IList<MilestoneDto>>(permission);

        var milestones = await db.Milestones
            .Where(m => m.ProjectId == command.Request.ProjectId)
            .OrderBy(m => m.Order)
            .Select(m => new MilestoneDto(
                m.Id, m.Name, m.Description, m.ProjectId, m.Order, m.CreatedAt, m.UpdatedAt, m.ClosedAt))
            .ToListAsync(ct);

        return Result.Ok((IList<MilestoneDto>)milestones);
    }
}
/// <summary>
/// Closes a milestone.
/// </summary>
public class CloseMilestoneHandler : ICommandHandler<CloseMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public CloseMilestoneHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<MilestoneDto>> HandleAsync(CloseMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Milestone, AuthAction.Execute,
            projectId: milestone.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<MilestoneDto>(permission);
        milestone.Close();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new MilestoneDto(
            milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
            milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt));
    }
}
/// <summary>
/// Clears a milestone's closed time.
/// </summary>
public class ReopenMilestoneHandler : ICommandHandler<ReopenMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ReopenMilestoneHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<MilestoneDto>> HandleAsync(ReopenMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Milestone, AuthAction.Execute,
            projectId: milestone.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<MilestoneDto>(permission);
        milestone.Reopen();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new MilestoneDto(
            milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
            milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt));
    }
}
