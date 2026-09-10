namespace ProjectBeacon.Application.Milestones;

using Application.Common;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class CreateMilestoneHandler : ICommandHandler<CreateMilestoneCommand, Result<MilestoneDto>>
{
    private readonly BeaconDbContext _db;

    public CreateMilestoneHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<MilestoneDto>> HandleAsync(CreateMilestoneCommand command, CancellationToken ct = default)
    {
        var projectExists = await _db.Projects.AnyAsync(p => p.Id == command.Request.ProjectId, ct);
        if (!projectExists)
            return Result.Failure<MilestoneDto>("Project not found.");

        var milestone = Domain.Entities.Projects.Milestone.Create(
            command.Request.Name,
            command.Request.Description,
            command.Request.ProjectId,
            command.Request.Order);

        _db.Milestones.Add(milestone);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt);
}

public class UpdateMilestoneHandler : ICommandHandler<UpdateMilestoneCommand, Result<MilestoneDto>>
{
    private readonly BeaconDbContext _db;

    public UpdateMilestoneHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<MilestoneDto>> HandleAsync(UpdateMilestoneCommand command, CancellationToken ct = default)
    {
        var milestone = await _db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");

        milestone.Update(command.Request.Name, command.Request.Description, command.Request.Order);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt);
}

public class DeleteMilestoneHandler : ICommandHandler<DeleteMilestoneCommand, Result<bool>>
{
    private readonly BeaconDbContext _db;

    public DeleteMilestoneHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<bool>> HandleAsync(DeleteMilestoneCommand command, CancellationToken ct = default)
    {
        var milestone = await _db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<bool>("Milestone not found.");

        _db.Milestones.Remove(milestone);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(true);
    }
}

public class GetMilestoneHandler : ICommandHandler<GetMilestoneCommand, Result<MilestoneDto>>
{
    private readonly BeaconDbContext _db;

    public GetMilestoneHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<MilestoneDto>> HandleAsync(GetMilestoneCommand command, CancellationToken ct = default)
    {
        var milestone = await _db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");

        return Result.Ok(MapToDto(milestone));
    }

    private static MilestoneDto MapToDto(Milestone milestone) =>
        new(milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId, milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt);
}

public class ListProjectMilestonesHandler : ICommandHandler<ListProjectMilestonesCommand, Result<IList<MilestoneDto>>>
{
    private readonly BeaconDbContext _db;

    public ListProjectMilestonesHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<IList<MilestoneDto>>> HandleAsync(ListProjectMilestonesCommand command, CancellationToken ct = default)
    {
        var milestones = await _db.Milestones
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
    private readonly BeaconDbContext _db;

    public CloseMilestoneHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<MilestoneDto>> HandleAsync(CloseMilestoneCommand command, CancellationToken ct = default)
    {
        var milestone = await _db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");
        milestone.Close();
        await _db.SaveChangesAsync(ct);
        return Result.Ok(new MilestoneDto(
            milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
            milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt));
    }
}

public class ReopenMilestoneHandler : ICommandHandler<ReopenMilestoneCommand, Result<MilestoneDto>>
{
    private readonly BeaconDbContext _db;

    public ReopenMilestoneHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<MilestoneDto>> HandleAsync(ReopenMilestoneCommand command, CancellationToken ct = default)
    {
        var milestone = await _db.Milestones.FindAsync([command.Request.MilestoneId], ct);
        if (milestone is null)
            return Result.Failure<MilestoneDto>("Milestone not found.");
        milestone.Reopen();
        await _db.SaveChangesAsync(ct);
        return Result.Ok(new MilestoneDto(
            milestone.Id, milestone.Name, milestone.Description, milestone.ProjectId,
            milestone.Order, milestone.CreatedAt, milestone.UpdatedAt, milestone.ClosedAt));
    }
}
