namespace ProjectBeacon.Application.Decisions;

using Application.Common;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Loads one decision from the specified project.</summary>
public sealed class GetDecisionHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetDecisionHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<DecisionDto>> HandleAsync(
        Guid projectId, Guid decisionId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var decision = await db.Decisions.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.Id == decisionId)
            .Select(item => new DecisionDto(item.Id, item.Title, item.Context, item.DecisionBody,
                item.Consequences, item.Status, item.ProjectId, item.SupersededById, item.Revision))
            .FirstOrDefaultAsync(ct);
        return decision is null
            ? Result.Failure<DecisionDto>("Decision not found.", ErrorKind.NotFound)
            : Result.Ok(decision);
    }
}
