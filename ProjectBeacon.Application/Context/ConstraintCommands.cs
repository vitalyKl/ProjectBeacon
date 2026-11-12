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
public record ConstraintDto(Guid Id, string Body, ConstraintKind Kind, ConstraintStatus Status, Guid ProjectId);
/// <summary>
/// Project, body, and kind for a new proposed constraint.
/// </summary>
public record CreateConstraintRequest(Guid ProjectId, string Body, ConstraintKind Kind);
/// <summary>
/// Proposes a constraint. The body is required.
/// </summary>
public class CreateConstraintHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public CreateConstraintHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<ConstraintDto>> HandleAsync(CreateConstraintRequest request, ActorContext actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return Result.Failure<ConstraintDto>("Constraint body is required.");

        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Constraint, AuthAction.Create,
            projectId: request.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<ConstraintDto>(permission);

        var constraint = Constraint.Create(request.Body.Trim(), request.Kind, request.ProjectId);
        db.Constraints.Add(constraint);
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(constraint));
    }

    public async Task<Result<ConstraintDto>> HandleProposalAsync(CreateConstraintRequest request, ActorContext actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return Result.Failure<ConstraintDto>("Constraint body is required.");

        await using var db = _dbFactory.CreateDbContext();
        var permission = await KnowledgeWriteAuthorization.CanWriteAsync(
            db, _authorization, actor, ResourceType.Constraint, AuthAction.Create,
            request.ProjectId, ct);
        if (!permission.Success)
            return Result.Failure<ConstraintDto>(permission);

        var body = request.Body.Trim();
        var existing = await db.Constraints.FirstOrDefaultAsync(c =>
            c.ProjectId == request.ProjectId && c.Body == body && c.Kind == request.Kind
            && c.Status == ConstraintStatus.Proposed, ct);
        if (existing is not null)
            return Result.Ok(Map(existing));

        var constraint = Constraint.Create(body, request.Kind, request.ProjectId);
        db.Constraints.Add(constraint);
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(constraint));
    }

    private static ConstraintDto Map(Constraint c) =>
        new(c.Id, c.Body, c.Kind, c.Status, c.ProjectId);
}
/// <summary>
/// Lists constraints for the current project.
/// </summary>
public class ListConstraintsHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ListConstraintsHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<IList<ConstraintDto>>> HandleAsync(Guid projectId, ActorContext actor, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Constraint, AuthAction.Read,
            projectId: projectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<IList<ConstraintDto>>(permission);

        var items = await db.Constraints
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new ConstraintDto(c.Id, c.Body, c.Kind, c.Status, c.ProjectId))
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

    public ActivateConstraintHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<ConstraintDto>> HandleAsync(Guid constraintId, ActorContext actor, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var constraint = await db.Constraints.FindAsync([constraintId], ct);
        if (constraint is null)
            return Result.Failure<ConstraintDto>("Constraint not found.");
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Constraint, AuthAction.Execute,
            projectId: constraint.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<ConstraintDto>(permission);
        constraint.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new ConstraintDto(constraint.Id, constraint.Body, constraint.Kind, constraint.Status, constraint.ProjectId));
    }
}
/// <summary>
/// Marks a constraint rejected.
/// </summary>
public class RejectConstraintHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public RejectConstraintHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<ConstraintDto>> HandleAsync(Guid constraintId, ActorContext actor, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var constraint = await db.Constraints.FindAsync([constraintId], ct);
        if (constraint is null)
            return Result.Failure<ConstraintDto>("Constraint not found.");
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Constraint, AuthAction.Execute,
            projectId: constraint.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<ConstraintDto>(permission);
        constraint.Reject();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new ConstraintDto(constraint.Id, constraint.Body, constraint.Kind, constraint.Status, constraint.ProjectId));
    }
}