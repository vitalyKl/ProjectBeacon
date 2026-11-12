namespace ProjectBeacon.Application.Decisions;

using Application.Common;
using Application.Authorization;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Loads one decision from the specified project.</summary>
public sealed class GetDecisionHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public GetDecisionHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<DecisionDto>> HandleAsync(
        Guid projectId, Guid decisionId, ActorContext actor, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Decision, AuthAction.Read,
            projectId: projectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<DecisionDto>(permission);

        var decision = await db.Decisions.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.Id == decisionId)
            .Select(item => new DecisionDto(item.Id, item.Title, item.Context, item.DecisionBody,
                item.Consequences, item.Status, item.ProjectId, item.SupersededById))
            .FirstOrDefaultAsync(ct);
        return decision is null
            ? Result.Failure<DecisionDto>("Decision not found.", ErrorKind.NotFound)
            : Result.Ok(decision);
    }
}
