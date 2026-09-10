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
    private readonly BeaconDbContext _db;

    public CreateDecisionHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<DecisionDto>> HandleAsync(CreateDecisionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
            return Result.Failure<DecisionDto>("Title and body are required.");

        var decision = Decision.Create(
            request.Title.Trim(),
            request.Context.Trim(),
            request.Body.Trim(),
            request.ProjectId,
            string.IsNullOrWhiteSpace(request.Consequences) ? null : request.Consequences.Trim());
        _db.Decisions.Add(decision);
        await _db.SaveChangesAsync(ct);
        return Result.Ok(Map(decision));
    }

    private static DecisionDto Map(Decision d) =>
        new(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId);
}

public class ListDecisionsHandler
{
    private readonly BeaconDbContext _db;

    public ListDecisionsHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<IList<DecisionDto>>> HandleAsync(Guid projectId, CancellationToken ct = default)
    {
        var items = await _db.Decisions
            .Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DecisionDto(d.Id, d.Title, d.Context, d.DecisionBody, d.Consequences, d.Status, d.ProjectId))
            .ToListAsync(ct);
        return Result.Ok((IList<DecisionDto>)items);
    }
}

public class AcceptDecisionHandler
{
    private readonly BeaconDbContext _db;

    public AcceptDecisionHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<DecisionDto>> HandleAsync(Guid decisionId, CancellationToken ct = default)
    {
        var decision = await _db.Decisions.FindAsync([decisionId], ct);
        if (decision is null)
            return Result.Failure<DecisionDto>("Decision not found.");
        decision.Accept();
        await _db.SaveChangesAsync(ct);
        return Result.Ok(new DecisionDto(
            decision.Id, decision.Title, decision.Context, decision.DecisionBody,
            decision.Consequences, decision.Status, decision.ProjectId));
    }
}
