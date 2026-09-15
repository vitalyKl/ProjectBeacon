namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record ConstraintDto(Guid Id, string Body, ConstraintKind Kind, ConstraintStatus Status, Guid ProjectId);

public record CreateConstraintRequest(Guid ProjectId, string Body, ConstraintKind Kind);

public class CreateConstraintHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CreateConstraintHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ConstraintDto>> HandleAsync(CreateConstraintRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return Result.Failure<ConstraintDto>("Constraint body is required.");

        await using var db = _dbFactory.CreateDbContext();
        var constraint = Constraint.Create(request.Body.Trim(), request.Kind, request.ProjectId);
        db.Constraints.Add(constraint);
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(constraint));
    }

    private static ConstraintDto Map(Constraint c) =>
        new(c.Id, c.Body, c.Kind, c.Status, c.ProjectId);
}

public class ListConstraintsHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListConstraintsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ConstraintDto>>> HandleAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var items = await db.Constraints
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ConstraintDto(c.Id, c.Body, c.Kind, c.Status, c.ProjectId))
            .ToListAsync(ct);
        return Result.Ok((IList<ConstraintDto>)items);
    }
}

public class ActivateConstraintHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ActivateConstraintHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ConstraintDto>> HandleAsync(Guid constraintId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var constraint = await db.Constraints.FindAsync([constraintId], ct);
        if (constraint is null)
            return Result.Failure<ConstraintDto>("Constraint not found.");
        constraint.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new ConstraintDto(constraint.Id, constraint.Body, constraint.Kind, constraint.Status, constraint.ProjectId));
    }
}

public class RejectConstraintHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public RejectConstraintHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ConstraintDto>> HandleAsync(Guid constraintId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var constraint = await db.Constraints.FindAsync([constraintId], ct);
        if (constraint is null)
            return Result.Failure<ConstraintDto>("Constraint not found.");
        constraint.Reject();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new ConstraintDto(constraint.Id, constraint.Body, constraint.Kind, constraint.Status, constraint.ProjectId));
    }
}