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
    Guid? SupersededById = null);
/// <summary>
/// Project, title, context, body, and optional consequences for a proposed decision.
/// </summary>
public record CreateDecisionRequest(Guid ProjectId, string Title, string Context, string Body, string? Consequences);
/// <summary>
/// Records a proposed decision. Title and body are required.
/// </summary>
public class CreateDecisionHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public CreateDecisionHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<DecisionDto>> HandleAsync(CreateDecisionRequest request, ActorContext actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
            return Result.Failure<DecisionDto>("Title and body are required.");

        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Decision, AuthAction.Create,
            projectId: request.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<DecisionDto>(permission);

        var decision = Decision.Create(
            request.Title.Trim(),
            request.Context.Trim(),
            request.Body.Trim(),
            request.ProjectId,
            string.IsNullOrWhiteSpace(request.Consequences) ? null : request.Consequences.Trim());
        db.Decisions.Add(decision);
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(decision));
    }

    public async Task<Result<DecisionDto>> HandleProposalAsync(CreateDecisionRequest request, ActorContext actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
            return Result.Failure<DecisionDto>("Title and body are required.");

        await using var db = _dbFactory.CreateDbContext();
        var permission = await KnowledgeWriteAuthorization.CanWriteAsync(
            db, _authorization, actor, ResourceType.Decision, AuthAction.Create,
            request.ProjectId, ct);
        if (!permission.Success)
            return Result.Failure<DecisionDto>(permission);

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
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(decision));
    }

    private static DecisionDto Map(Decision d) =>
        new(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId, d.SupersededById);
}
/// <summary>
/// Lists decisions for the project.
/// </summary>
public class ListDecisionsHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public ListDecisionsHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<IList<DecisionDto>>> HandleAsync(Guid projectId, ActorContext actor, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Decision, AuthAction.Read,
            projectId: projectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<IList<DecisionDto>>(permission);

        var items = await db.Decisions
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DecisionDto(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId, d.SupersededById))
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

    public AcceptDecisionHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<DecisionDto>> HandleAsync(Guid decisionId, ActorContext actor, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var decision = await db.Decisions.FindAsync([decisionId], ct);
        if (decision is null)
            return Result.Failure<DecisionDto>("Decision not found.");
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Decision, AuthAction.Execute,
            projectId: decision.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<DecisionDto>(permission);
        decision.Accept();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new DecisionDto(
            decision.Id, decision.Title, decision.Context, decision.DecisionBody,
            decision.Consequences, decision.Status, decision.ProjectId, decision.SupersededById));
    }
}
/// <summary>
/// Marks a decision deprecated.
/// </summary>
public class DeprecateDecisionHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public DeprecateDecisionHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<DecisionDto>> HandleAsync(Guid decisionId, ActorContext actor, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var decision = await db.Decisions.FindAsync([decisionId], ct);
        if (decision is null)
            return Result.Failure<DecisionDto>("Decision not found.");
        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Decision, AuthAction.Execute,
            projectId: decision.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<DecisionDto>(permission);
        decision.Deprecate();
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(decision));
    }

    private static DecisionDto Map(Decision d) =>
        new(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId, d.SupersededById);
}
/// <summary>
/// Supersedes an accepted decision with a different one. The domain rejects a decision that is not accepted.
/// </summary>
public class SupersedeDecisionHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public SupersedeDecisionHandler(IBeaconDbFactory dbFactory, IAuthorizationService? authorization = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization ?? new AuthorizationService();
    }

    public async Task<Result<DecisionDto>> HandleAsync(Guid decisionId, Guid replacementId, ActorContext actor, CancellationToken ct = default)
    {
        if (decisionId == replacementId)
            return Result.Failure<DecisionDto>("Replacement must be a different decision.");

        await using var db = _dbFactory.CreateDbContext();
        var decision = await db.Decisions.FindAsync([decisionId], ct);
        if (decision is null)
            return Result.Failure<DecisionDto>("Decision not found.");
        var replacement = await db.Decisions.FindAsync([replacementId], ct);
        if (replacement is null || replacement.ProjectId != decision.ProjectId)
            return Result.Failure<DecisionDto>("Replacement not found.");

        var permission = await _authorization.CanAsync(
            db, actor, ResourceType.Decision, AuthAction.Execute,
            projectId: decision.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Failure<DecisionDto>(permission);

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
            decision.Consequences, decision.Status, decision.ProjectId, decision.SupersededById));
    }
}