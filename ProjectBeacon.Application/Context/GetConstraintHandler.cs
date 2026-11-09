namespace ProjectBeacon.Application.Context;

using Application.Common;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Loads one constraint from the specified project.</summary>
public sealed class GetConstraintHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetConstraintHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ConstraintDto>> HandleAsync(
        Guid projectId, Guid constraintId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var constraint = await db.Constraints.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.Id == constraintId)
            .Select(item => new ConstraintDto(item.Id, item.Body, item.Kind, item.Status, item.ProjectId, item.Revision))
            .FirstOrDefaultAsync(ct);
        return constraint is null
            ? Result.Failure<ConstraintDto>("Constraint not found.", ErrorKind.NotFound)
            : Result.Ok(constraint);
    }
}
