namespace ProjectBeacon.Application.Decisions;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record DecisionDto(
    Guid Id,
    string Title,
    string Context,
    string DecisionBody,
    string? Consequences,
    DecisionStatus Status,
    Guid ProjectId);

public record CreateDecisionRequest(Guid ProjectId, string Title, string Context, string Body, string? Consequences);

public class CreateDecisionHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CreateDecisionHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(decision));
    }

    private static DecisionDto Map(Decision d) =>
        new(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId);
}

public class ListDecisionsHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListDecisionsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<DecisionDto>>> HandleAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var items = await db.Decisions
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DecisionDto(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId))
            .ToListAsync(ct);
        return Result.Ok((IList<DecisionDto>)items);
    }
}

public class AcceptDecisionHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public AcceptDecisionHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<DecisionDto>> HandleAsync(Guid decisionId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var decision = await db.Decisions.FindAsync([decisionId], ct);
        if (decision is null)
            return Result.Failure<DecisionDto>("Decision not found.");
        decision.Accept();
        await db.SaveChangesAsync(ct);
        return Result.Ok(new DecisionDto(
            decision.Id, decision.Title, decision.Context, decision.DecisionBody,
            decision.Consequences, decision.Status, decision.ProjectId));
    }
}

public class DeprecateDecisionHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public DeprecateDecisionHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<DecisionDto>> HandleAsync(Guid decisionId, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var decision = await db.Decisions.FindAsync([decisionId], ct);
        if (decision is null)
            return Result.Failure<DecisionDto>("Decision not found.");
        decision.Deprecate();
        await db.SaveChangesAsync(ct);
        return Result.Ok(Map(decision));
    }

    private static DecisionDto Map(Decision d) =>
        new(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId);
}

public class SupersedeDecisionHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public SupersedeDecisionHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<DecisionDto>> HandleAsync(Guid decisionId, Guid replacementId, CancellationToken ct = default)
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
            decision.Consequences, decision.Status, decision.ProjectId));
    }
}