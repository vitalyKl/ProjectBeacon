namespace ProjectBeacon.Application.Milestones;

using Application.Common;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class CreateMilestoneHandler : ICommandHandler<CreateMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CreateMilestoneHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<MilestoneDto>> HandleAsync(CreateMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var projectExists = await db.Projects.AnyAsync(p => p.Id == command.Request.ProjectId, ct);
        if (!projectExists)
            return Result.Failure<MilestoneDto>("Project not found.");

        var milestone = Domain.Entities.Projects.Milestone.Create(
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

public class UpdateMilestoneHandler : ICommandHandler<UpdateMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public UpdateMilestoneHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<MilestoneDto>> HandleAsync(UpdateMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");

        milestone.Update(command.Request.Name, command.Request.Description, command.Request.Order);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt);
}

public class DeleteMilestoneHandler : ICommandHandler<DeleteMilestoneCommand, Result>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DeleteMilestoneHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(DeleteMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure("Milestone not found.");

        db.Milestones.Remove(milestone);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}

public class GetMilestoneHandler : ICommandHandler<GetMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetMilestoneHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<MilestoneDto>> HandleAsync(GetMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt);
}

public class ListProjectMilestonesHandler : ICommandHandler<ListProjectMilestonesCommand, Result<IList<MilestoneDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListProjectMilestonesHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<MilestoneDto>>> HandleAsync(ListProjectMilestonesCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestones = await db.Milestones
            .Where(m => m.ProjectId == command.Request.ProjectId)
            .OrderBy(m => m.Order)
            .Select(m => new MilestoneDto(
                m.Id, m.Name, m.Description, m.ProjectId, m.Order, m.CreatedAt, m.UpdatedAt, m.ClosedAt))
            .ToListAsync(ct);

        return Result.Ok((IList<MilestoneDto>)milestones);
    }
}

public class CloseMilestoneHandler : ICommandHandler<CloseMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CloseMilestoneHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<MilestoneDto>> HandleAsync(CloseMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");
        milestone.Close();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new MilestoneDto(
            milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
            milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt));
    }
}

public class ReopenMilestoneHandler : ICommandHandler<ReopenMilestoneCommand, Result<MilestoneDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ReopenMilestoneHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<MilestoneDto>> HandleAsync(ReopenMilestoneCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var milestone = await db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");
        milestone.Reopen();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new MilestoneDto(
            milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
            milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt));
    }
}