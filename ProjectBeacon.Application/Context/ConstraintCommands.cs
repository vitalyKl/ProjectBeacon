namespace ProjectBeacon.Application.Context;

using Application.Common;
using Application.Authorization;
using Domain.Entities.Context;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// A project constraint and whether it is still proposed.
/// </summary>
public record ConstraintDto(Guid Id, string Body, ConstraintKind Kind, ConstraintStatus Status, Guid ProjectId, int Revision = 1);
/// <summary>
/// Project, body, and kind for a new proposed constraint.
/// </summary>
public record CreateConstraintRequest(Guid ProjectId, string Body, ConstraintKind Kind, ActorContext? Actor = null, string? AgentId = null);
/// <summary>
/// Proposes a constraint. The body is required.
/// </summary>
public class CreateConstraintHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public CreateConstraintHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public CreateConstraintHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<ConstraintDto>> HandleAsync(CreateConstraintRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return Result.Failure<ConstraintDto>("Constraint body is required.");

        await using var db = _dbFactory.CreateDbContext();
        var constraint = Constraint.Create(request.Body.Trim(), request.Kind, request.ProjectId);
        db.Constraints.Add(constraint);
        var attribution = ContentAttribution.FromActor(request.Actor, request.AgentId);
        var saved = await _contentWriter.SaveAsync(db, constraint, attribution.Type, attribution.Id, ct);
        return saved.Success
            ? Result.Ok(Map(constraint))
            : Result.Failure<ConstraintDto>(saved.Error ?? "Constraint version could not be saved.", ErrorKind.Conflict);
    }

    public async Task<Result<ConstraintDto>> HandleProposalAsync(CreateConstraintRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return Result.Failure<ConstraintDto>("Constraint body is required.");

        await using var db = _dbFactory.CreateDbContext();
        var body = request.Body.Trim();
        var existing = await db.Constraints.FirstOrDefaultAsync(c =>
            c.ProjectId == request.ProjectId && c.Body == body && c.Kind == request.Kind
            && c.Status == ConstraintStatus.Proposed, ct);
        if (existing is not null)
            return Result.Ok(Map(existing));

        var constraint = Constraint.Create(body, request.Kind, request.ProjectId);
        db.Constraints.Add(constraint);
        var attribution = ContentAttribution.FromActor(request.Actor, request.AgentId);
        var saved = await _contentWriter.SaveAsync(db, constraint, attribution.Type, attribution.Id, ct);
        return saved.Success
            ? Result.Ok(Map(constraint))
            : Result.Failure<ConstraintDto>(saved.Error ?? "Constraint version could not be saved.", ErrorKind.Conflict);
    }

    private static ConstraintDto Map(Constraint c) =>
        new(c.Id, c.Body, c.Kind, c.Status, c.ProjectId, c.Revision);
}
/// <summary>
/// Lists constraints for the current project.
/// </summary>
public class ListConstraintsHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListConstraintsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ConstraintDto>>> HandleAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var items = await db.Constraints
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ConstraintDto(c.Id, c.Body, c.Kind, c.Status, c.ProjectId, c.Revision))
            .ToListAsync(ct);
        return Result.Ok((IList<ConstraintDto>)items);
    }
}
/// <summary>
/// Marks a proposed constraint active.
/// </summary>
public class ActivateConstraintHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ActivateConstraintHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<ConstraintDto>> HandleAsync(
        Guid constraintId, ActorContext actor, CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<ConstraintDto>("Only a human Owner/Admin can activate a constraint.");

        await using var db = _dbFactory.CreateDbContext();
        var constraint = await db.Constraints.FindAsync([constraintId], ct);
        if (constraint is null)
            return Result.Failure<ConstraintDto>("Constraint not found.");
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer,
            projectId: constraint.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Forbidden<ConstraintDto>("Only a project or organization Owner/Admin can activate constraints.");

        constraint.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new ConstraintDto(constraint.Id, constraint.Body, constraint.Kind, constraint.Status, constraint.ProjectId, constraint.Revision));
    }
}
/// <summary>
/// Marks a constraint rejected.
/// </summary>
public class RejectConstraintHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public RejectConstraintHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<ConstraintDto>> HandleAsync(
        Guid constraintId, ActorContext actor, CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<ConstraintDto>("Only a human Owner/Admin can reject a constraint.");

        await using var db = _dbFactory.CreateDbContext();
        var constraint = await db.Constraints.FindAsync([constraintId], ct);
        if (constraint is null)
            return Result.Failure<ConstraintDto>("Constraint not found.");
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer,
            projectId: constraint.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Forbidden<ConstraintDto>("Only a project or organization Owner/Admin can reject constraints.");

        constraint.Reject();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new ConstraintDto(constraint.Id, constraint.Body, constraint.Kind, constraint.Status, constraint.ProjectId, constraint.Revision));
    }
}