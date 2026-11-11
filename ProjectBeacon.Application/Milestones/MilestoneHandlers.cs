namespace ProjectBeacon.Application.Milestones;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Application.Persistence;
using Application.Proposals;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Creates a milestone on a project.
/// </summary>
public class CreateMilestoneHandler : ICommandHandler<CreateMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public CreateMilestoneHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public CreateMilestoneHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<MilestoneDto>> HandleAsync(CreateMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var projectExists = await db.Projects.AnyAsync(p => p.Id == command.Request.ProjectId, ct);
        if (!projectExists)
            return Result.Failure<MilestoneDto>("Project not found.");

        var milestone = Domain.Entities.Roadmap.Milestone.Create(
            command.Request.Name,
            command.Request.Description,
            command.Request.ProjectId,
            command.Request.Order);

        db.Milestones.Add(milestone);
        var attribution = ContentAttribution.FromActor(command.Actor);
        var saved = await _contentWriter.SaveAsync(db, milestone, attribution.Type, attribution.Id, ct);
        if (!saved.Success)
            return Result.Failure<MilestoneDto>(saved.Error ?? "Milestone version could not be saved.", ErrorKind.Conflict);

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt, milestone.Revision);
}
/// <summary>
/// Updates a milestone name, description, or order.
/// </summary>
public class UpdateMilestoneHandler : ICommandHandler<UpdateMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public UpdateMilestoneHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public UpdateMilestoneHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<MilestoneDto>> HandleAsync(UpdateMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");

        var contentChanged =
            (command.Request.Name is not null && !string.Equals(milestone.Name, command.Request.Name, StringComparison.Ordinal))
            || (command.Request.Description is not null && !string.Equals(milestone.Description, command.Request.Description, StringComparison.Ordinal))
            || (command.Request.Order is not null && milestone.Order != command.Request.Order.Value);
        if (contentChanged && command.Request.ExpectedRevision != milestone.Revision)
            return Result.Failure<MilestoneDto>(
                "Milestone changed since it was loaded. Refresh it and retry.",
                ErrorKind.Conflict);

        milestone.Update(command.Request.Name, command.Request.Description, command.Request.Order);
        if (contentChanged)
        {
            var attribution = ContentAttribution.FromActor(command.Actor);
            var saved = await _contentWriter.SaveAsync(
                db, milestone, attribution.Type ?? "Human", attribution.Id, ct);
            if (!saved.Success)
                return Result.Failure<MilestoneDto>(saved.Error ?? "Milestone content conflict.", ErrorKind.Conflict);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt, milestone.Revision);
}
/// <summary>
/// Deletes a milestone.
/// </summary>
public class DeleteMilestoneHandler : ICommandHandler<DeleteMilestoneCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public DeleteMilestoneHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public DeleteMilestoneHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result> HandleAsync(DeleteMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure("Milestone not found.");
        if (command.Actor is not { IsHuman: true, UserId: not null } actor
            || !await ProposalProjectAuthorization.CanWriteAsync(db, actor, milestone.ProjectId, ct))
            return Result.Forbidden("Project membership is required to delete a milestone.");

        milestone.Delete();
        db.Milestones.Remove(milestone);
        var attribution = ContentAttribution.FromActor(actor);
        var saved = await _contentWriter.SaveAsync(
            db, milestone, attribution.Type ?? "Human", attribution.Id, ct, deleted: true);
        return saved.Success
            ? Result.Ok()
            : Result.Failure(saved.Error ?? "Milestone delete conflict.", ErrorKind.Conflict);
    }
}
/// <summary>
/// Loads one milestone.
/// </summary>
public class GetMilestoneHandler : ICommandHandler<GetMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetMilestoneHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<MilestoneDto>> HandleAsync(GetMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt, milestone.Revision);
}
/// <summary>
/// Lists milestones for a project.
/// </summary>
public class ListProjectMilestonesHandler : ICommandHandler<ListProjectMilestonesCommand, Result<IList<MilestoneDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListProjectMilestonesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<MilestoneDto>>> HandleAsync(ListProjectMilestonesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestones = await db.Milestones
            .Where(m => m.ProjectId == command.Request.ProjectId)
            .OrderBy(m => m.Order)
            .Select(m => new MilestoneDto(
                m.Id, m.Name, m.Description, m.ProjectId, m.Order, m.CreatedAt, m.UpdatedAt, m.ClosedAt, m.Revision))
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
    private readonly ContentWriter _contentWriter;

    public CloseMilestoneHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public CloseMilestoneHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<MilestoneDto>> HandleAsync(CloseMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");
        var previousRevision = milestone.Revision;
        milestone.Close();
        if (milestone.Revision != previousRevision)
        {
            var saved = await _contentWriter.SaveAsync(db, milestone, "Human", null, ct);
            if (!saved.Success)
                return Result.Failure<MilestoneDto>(saved.Error ?? "Milestone close conflict.", ErrorKind.Conflict);
        }
        return Result.Ok(new MilestoneDto(
            milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
            milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt, milestone.Revision));
    }
}
/// <summary>
/// Clears a milestone's closed time.
/// </summary>
public class ReopenMilestoneHandler : ICommandHandler<ReopenMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public ReopenMilestoneHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public ReopenMilestoneHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<MilestoneDto>> HandleAsync(ReopenMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");
        var previousRevision = milestone.Revision;
        milestone.Reopen();
        if (milestone.Revision != previousRevision)
        {
            var saved = await _contentWriter.SaveAsync(db, milestone, "Human", null, ct);
            if (!saved.Success)
                return Result.Failure<MilestoneDto>(saved.Error ?? "Milestone reopen conflict.", ErrorKind.Conflict);
        }
        return Result.Ok(new MilestoneDto(
            milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
            milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt, milestone.Revision));
    }
}
