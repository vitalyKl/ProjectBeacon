namespace ProjectBeacon.Application.Context;

using Application.Common;
using Application.Authorization;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Loads one constraint from the specified project.</summary>
public sealed class GetConstraintHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public GetConstraintHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<ConstraintDto>> HandleAsync(
        Guid projectId, Guid constraintId, ActorContext actor, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Constraint, AuthAction.Read,
            projectId: projectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<ConstraintDto>(permission);

        var constraint = await db.Constraints.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.Id == constraintId)
            .Select(item => new ConstraintDto(item.Id, item.Body, item.Kind, item.Status, item.ProjectId))
            .FirstOrDefaultAsync(ct);
        return constraint is null
            ? Result.Failure<ConstraintDto>("Constraint not found.", ErrorKind.NotFound)
            : Result.Ok(constraint);
    }
}
