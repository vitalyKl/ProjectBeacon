namespace ProjectBeacon.Application.Decisions;

using Application.Common;
using Application.Authorization;
using Domain.Entities.Context;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// A decision, its status, and the decision that superseded it when one did.
/// </summary>
public record DecisionDto(
    Guid Id,
    string Title,
    string Context,
    string DecisionBody,
    string? Consequences,
    DecisionStatus Status,
    Guid ProjectId,
    Guid? SupersededById = null,
    int Revision = 1);
/// <summary>
/// Project, title, context, body, and optional consequences for a proposed decision.
/// </summary>
public record CreateDecisionRequest(Guid ProjectId, string Title, string Context, string Body, string? Consequences, ActorContext? Actor = null, string? AgentId = null);
/// <summary>
/// Records a proposed decision. Title and body are required.
/// </summary>
public class CreateDecisionHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;

    public CreateDecisionHandler(IBeaconDbFactory dbFactory)
        : this(dbFactory, new ContentWriter())
    {
    }

    public CreateDecisionHandler(IBeaconDbFactory dbFactory, ContentWriter contentWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
    }

    public async Task<Result<DecisionDto>> HandleAsync(CreateDecisionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
            return Result.Failure<DecisionDto>("Title and body are required.");

        await using var db = _dbFactory.CreateDbContext();
        var decision = Decision.Create(
            request.Title.Trim(),
            request.Context.Trim(),
            request.Body.Trim(),
            request.ProjectId,
            string.IsNullOrWhiteSpace(request.Consequences) ? null : request.Consequences.Trim());
        db.Decisions.Add(decision);
        var attribution = ContentAttribution.FromActor(request.Actor, request.AgentId);
        var saved = await _contentWriter.SaveAsync(db, decision, attribution.Type, attribution.Id, ct);
        return saved.Success
            ? Result.Ok(Map(decision))
            : Result.Failure<DecisionDto>(saved.Error ?? "Decision version could not be saved.", ErrorKind.Conflict);
    }

    public async Task<Result<DecisionDto>> HandleProposalAsync(CreateDecisionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
            return Result.Failure<DecisionDto>("Title and body are required.");

        await using var db = _dbFactory.CreateDbContext();
        var title = request.Title.Trim();
        var context = request.Context?.Trim() ?? string.Empty;
        var body = request.Body.Trim();
        var consequences = string.IsNullOrWhiteSpace(request.Consequences) ? null : request.Consequences.Trim();
        var existing = await db.Decisions.FirstOrDefaultAsync(d =>
            d.ProjectId == request.ProjectId && d.Title == title && d.Context == context
            && d.DecisionBody == body && d.Consequences == consequences
            && d.Status == DecisionStatus.Proposed, ct);
        if (existing is not null)
            return Result.Ok(Map(existing));

        var decision = Decision.Create(title, context, body, request.ProjectId, consequences);
        db.Decisions.Add(decision);
        var attribution = ContentAttribution.FromActor(request.Actor, request.AgentId);
        var saved = await _contentWriter.SaveAsync(db, decision, attribution.Type, attribution.Id, ct);
        return saved.Success
            ? Result.Ok(Map(decision))
            : Result.Failure<DecisionDto>(saved.Error ?? "Decision version could not be saved.", ErrorKind.Conflict);
    }

    private static DecisionDto Map(Decision d) =>
        new(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId, d.SupersededById, d.Revision);
}
/// <summary>
/// Lists decisions for the project.
/// </summary>
public class ListDecisionsHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListDecisionsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<DecisionDto>>> HandleAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var items = await db.Decisions
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DecisionDto(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId, d.SupersededById, d.Revision))
            .ToListAsync(ct);
        return Result.Ok((IList<DecisionDto>)items);
    }
}
/// <summary>
/// Accepts a proposed decision.
/// </summary>
public class AcceptDecisionHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public AcceptDecisionHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<DecisionDto>> HandleAsync(
        Guid decisionId, ActorContext actor, CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<DecisionDto>("Only a human Owner/Admin can accept a decision.");

        await using var db = _dbFactory.CreateDbContext();
        var decision = await db.Decisions.FindAsync([decisionId], ct);
        if (decision is null)
            return Result.Failure<DecisionDto>("Decision not found.");
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer,
            projectId: decision.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Forbidden<DecisionDto>("Only a project or organization Owner/Admin can accept decisions.");

        decision.Accept();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new DecisionDto(
            decision.Id, decision.Title, decision.Context, decision.DecisionBody,
            decision.Consequences, decision.Status, decision.ProjectId, decision.SupersededById, decision.Revision));
    }
}
/// <summary>
/// Marks a decision deprecated.
/// </summary>
public class DeprecateDecisionHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public DeprecateDecisionHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<DecisionDto>> HandleAsync(
        Guid decisionId, ActorContext actor, CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<DecisionDto>("Only a human Owner/Admin can deprecate a decision.");

        await using var db = _dbFactory.CreateDbContext();
        var decision = await db.Decisions.FindAsync([decisionId], ct);
        if (decision is null)
            return Result.Failure<DecisionDto>("Decision not found.");
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer,
            projectId: decision.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Forbidden<DecisionDto>("Only a project or organization Owner/Admin can deprecate decisions.");

        decision.Deprecate();
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(decision));
    }

    private static DecisionDto Map(Decision d) =>
        new(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId, d.SupersededById, d.Revision);
}
/// <summary>
/// Supersedes an accepted decision with a different one. The domain rejects a decision that is not accepted.
/// </summary>
public class SupersedeDecisionHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public SupersedeDecisionHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<DecisionDto>> HandleAsync(
        Guid decisionId, Guid replacementId, ActorContext actor, CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<DecisionDto>("Only a human Owner/Admin can supersede a decision.");
        if (decisionId == replacementId)
            return Result.Failure<DecisionDto>("Replacement must be a different decision.");

        await using var db = _dbFactory.CreateDbContext();
        var decision = await db.Decisions.FindAsync([decisionId], ct);
        if (decision is null)
            return Result.Failure<DecisionDto>("Decision not found.");
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Project, AuthAction.Administer,
            projectId: decision.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Forbidden<DecisionDto>("Only a project or organization Owner/Admin can supersede decisions.");

        var replacement = await db.Decisions.FindAsync([replacementId], ct);
        if (replacement is null || replacement.ProjectId != decision.ProjectId)
            return Result.Failure<DecisionDto>("Replacement not found.");

        try
        {
            decision.Supersede(replacement);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<DecisionDto>(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(new DecisionDto(
            decision.Id, decision.Title, decision.Context, decision.DecisionBody,
            decision.Consequences, decision.Status, decision.ProjectId, decision.SupersededById, decision.Revision));
    }
}