namespace ProjectBeacon.Application.Proposals;

using Application.Authorization;
using Application.Audit;
using Application.Common;
using Application.Persistence;
using Domain.Enums;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

/// <summary>Counts all human-reviewable content in one project's Inbox.</summary>
public sealed record ProposalInboxSummaryDto(
    int PendingProposals,
    int ConflictedProposals,
    int ProposedDecisions,
    int ProposedConstraints,
    int NeedsReviewContextSections)
{
    public int TotalPending =>
        PendingProposals + ProposedDecisions + ProposedConstraints + NeedsReviewContextSections;
}

/// <summary>Loads project-scoped Inbox counts after enforcing the same access boundary as the Inbox list.</summary>
public sealed class GetProposalInboxSummaryHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetProposalInboxSummaryHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ProposalInboxSummaryDto>> HandleAsync(
        Guid projectId,
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (projectId == Guid.Empty || !actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<ProposalInboxSummaryDto>();

        await using var db = _dbFactory.CreateDbContext();
        if (!await ProposalProjectAuthorization.CanWriteAsync(db, actor, projectId, ct))
            return Result.Forbidden<ProposalInboxSummaryDto>();

        var now = DateTime.UtcNow;
        var pending = await db.Proposals.CountAsync(item =>
            item.ProjectId == projectId
            && item.Status == ProposalStatus.Pending
            && item.ExpiresAt > now, ct);
        var conflicted = await db.Proposals.CountAsync(item =>
            item.ProjectId == projectId
            && item.Status == ProposalStatus.Conflicted, ct);
        var decisions = await db.Decisions.CountAsync(item =>
            item.ProjectId == projectId
            && item.Status == DecisionStatus.Proposed, ct);
        var constraints = await db.Constraints.CountAsync(item =>
            item.ProjectId == projectId
            && item.Status == ConstraintStatus.Proposed, ct);
        var contextDrafts = await db.ContextSections.CountAsync(item =>
            item.ProjectId == projectId
            && item.ReviewState == ContextReviewState.NeedsReview, ct);

        return Result.Ok(new ProposalInboxSummaryDto(
            pending, conflicted, decisions, constraints, contextDrafts));
    }
}

/// <summary>Approves or rejects an existing NeedsReview context draft without bypassing revision checks.</summary>
public sealed class ReviewContextDraftHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ContentWriter _contentWriter;
    private readonly IAuditWriter _auditWriter;

    public ReviewContextDraftHandler(
        IBeaconDbFactory dbFactory,
        ContentWriter contentWriter,
        IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _contentWriter = contentWriter;
        _auditWriter = auditWriter;
    }

    public async Task<Result<ContextDraftReviewResult>> HandleAsync(
        ReviewContextDraftCommand command,
        CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<ContextDraftReviewResult>();
        if (command.ProjectId == Guid.Empty || command.ContextSectionId == Guid.Empty || command.ExpectedRevision < 1)
            return Result.Failure<ContextDraftReviewResult>("Project, context section, and expected revision are required.");

        await using var db = _dbFactory.CreateDbContext();
        if (!await ProposalProjectAuthorization.CanWriteAsync(db, command.Actor, command.ProjectId, ct))
            return Result.Forbidden<ContextDraftReviewResult>("Project membership is required to review context drafts.");

        var section = await db.ContextSections.FirstOrDefaultAsync(item =>
            item.Id == command.ContextSectionId && item.ProjectId == command.ProjectId, ct);
        if (section is null)
            return Result.Failure<ContextDraftReviewResult>("Context draft not found.");
        if (section.ReviewState != ContextReviewState.NeedsReview)
            return Result.Failure<ContextDraftReviewResult>("This context section is no longer awaiting review.", ErrorKind.Conflict);
        if (section.Revision != command.ExpectedRevision)
            return Result.Failure<ContextDraftReviewResult>("Context draft changed since it was loaded. Refresh and retry.", ErrorKind.Conflict);

        var orgId = await db.Projects
            .Where(project => project.Id == command.ProjectId)
            .Select(project => (Guid?)project.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is null)
            return Result.Failure<ContextDraftReviewResult>("Project not found.");

        var now = DateTime.UtcNow;
        if (command.Accept)
        {
            try
            {
                section.MarkReviewed(command.Actor.UserId.Value.ToString("D"));
            }
            catch (InvalidOperationException exception)
            {
                return Result.Failure<ContextDraftReviewResult>(exception.Message, ErrorKind.Conflict);
            }

            _auditWriter.Stage(db, AuditEventFactory.ForActor(
                command.Actor,
                "context.draft.approved",
                "ContextSection",
                section.Id.ToString("D"),
                orgId,
                command.ProjectId,
                detailsJson: JsonSerializer.Serialize(new { newState = ContextReviewState.Reviewed.ToString() })));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<ContextDraftReviewResult>(
                    "Context draft changed while it was being approved. Refresh and retry.", ErrorKind.Conflict);
            }

            return Result.Ok(new ContextDraftReviewResult(
                command.ContextSectionId, ContextReviewState.Reviewed, Deleted: false));
        }

        section.Delete();
        db.ContextSections.Remove(section);
        _auditWriter.Stage(db, AuditEventFactory.ForActor(
            command.Actor,
            "context.draft.rejected",
            "ContextSection",
            section.Id.ToString("D"),
            orgId,
            command.ProjectId,
            detailsJson: JsonSerializer.Serialize(new { newState = "Deleted" })));
        var saved = await _contentWriter.SaveAsync(
            db,
            section,
            "Human",
            command.Actor.UserId.Value.ToString("D"),
            ct,
            deleted: true);
        if (!saved.Success)
            return Result.Failure<ContextDraftReviewResult>(
                saved.Error ?? "Context draft could not be rejected.", ErrorKind.Conflict);

        return Result.Ok(new ContextDraftReviewResult(
            command.ContextSectionId, ContextReviewState.NeedsReview, Deleted: true));
    }
}

/// <summary>Review action for a legacy NeedsReview context section.</summary>
public sealed record ReviewContextDraftCommand(
    Guid ProjectId,
    Guid ContextSectionId,
    int ExpectedRevision,
    bool Accept,
    ActorContext Actor);

/// <summary>Result of reviewing a legacy context draft.</summary>
public sealed record ContextDraftReviewResult(
    Guid ContextSectionId,
    ContextReviewState ReviewState,
    bool Deleted);
