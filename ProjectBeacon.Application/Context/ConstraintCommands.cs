namespace ProjectBeacon.Application.Context;

using Application.Common;
using Application.Authorization;
using Domain.Entities.Context;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Application.Proposals;
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

        if (request.Actor is not { } actor)
            return Result.Forbidden<ConstraintDto>("A verified actor is required to propose a constraint.");

        await using var db = _dbFactory.CreateDbContext();
        if (!await ProposalProjectAuthorization.CanSubmitAsync(db, actor, request.ProjectId, ct))
            return Result.Forbidden<ConstraintDto>("Project membership is required to propose a constraint.");

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

        if (request.Actor is not { } actor)
            return Result.Forbidden<ConstraintDto>("A verified actor is required to propose a constraint.");

        await using var db = _dbFactory.CreateDbContext();
        if (!await ProposalProjectAuthorization.CanSubmitAsync(db, actor, request.ProjectId, ct))
            return Result.Forbidden<ConstraintDto>("Project membership is required to propose a constraint.");

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
    private readonly ContentWriter _contentWriter;

    public ActivateConstraintHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
        : this(dbFactory, authorization, new ContentWriter())
    {
    }

    public ActivateConstraintHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _contentWriter = contentWriter;
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

        var previousRevision = constraint.Revision;
        constraint.Activate();
        if (constraint.Revision == previousRevision)
            return Result.Ok(new ConstraintDto(constraint.Id, constraint.Body, constraint.Kind, constraint.Status, constraint.ProjectId, constraint.Revision));

        var attribution = ContentAttribution.FromActor(actor);
        var saved = await _contentWriter.SaveAsync(
            db, constraint, attribution.Type ?? "Human", attribution.Id, ct);
        return saved.Success
            ? Result.Ok(new ConstraintDto(constraint.Id, constraint.Body, constraint.Kind, constraint.Status, constraint.ProjectId, constraint.Revision))
            : Result.Failure<ConstraintDto>(saved.Error ?? "Constraint activation conflict.", ErrorKind.Conflict);
    }
}
/// <summary>
/// Marks a constraint rejected.
/// </summary>
public class RejectConstraintHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly ContentWriter _contentWriter;

    public RejectConstraintHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
        : this(dbFactory, authorization, new ContentWriter())
    {
    }

    public RejectConstraintHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _contentWriter = contentWriter;
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

        var previousRevision = constraint.Revision;
        constraint.Reject();
        if (constraint.Revision == previousRevision)
            return Result.Ok(new ConstraintDto(constraint.Id, constraint.Body, constraint.Kind, constraint.Status, constraint.ProjectId, constraint.Revision));

        var attribution = ContentAttribution.FromActor(actor);
        var saved = await _contentWriter.SaveAsync(
            db, constraint, attribution.Type ?? "Human", attribution.Id, ct);
        return saved.Success
            ? Result.Ok(new ConstraintDto(constraint.Id, constraint.Body, constraint.Kind, constraint.Status, constraint.ProjectId, constraint.Revision))
            : Result.Failure<ConstraintDto>(saved.Error ?? "Constraint rejection conflict.", ErrorKind.Conflict);
    }
}