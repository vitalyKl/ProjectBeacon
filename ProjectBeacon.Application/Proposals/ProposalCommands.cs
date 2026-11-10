namespace ProjectBeacon.Application.Proposals;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Application.Tasks;
using Domain.Common;
using Domain.Entities.Content;
using Domain.Entities.Context;
using Domain.Entities.Projects;
using Domain.Entities.Roadmap;
using Domain.Entities.Tasks;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Untrusted content proposal submitted by a human or a task-bound Actor session.</summary>
public sealed record CreateProposalRequest(
    ContentTargetKind TargetKind,
    Guid TargetId,
    ProposalOperation Operation,
    int BaseRevision,
    string PayloadJson,
    string RationaleMarkdown,
    string IdempotencyKey);

public sealed record CreateProposalCommand(
    Guid ProjectId,
    CreateProposalRequest Request,
    ActorContext Actor,
    Guid? PipelineTaskId = null,
    Guid? SubtaskId = null,
    Guid? SessionId = null,
    string? PipelineRole = null) : ICommand<Result<ProposalDto>>;

public sealed record AcceptProposalCommand(
    Guid ProjectId,
    Guid ProposalId,
    ActorContext Actor,
    string? AcceptedPayloadJson = null);

public sealed record RejectProposalCommand(
    Guid ProjectId,
    Guid ProposalId,
    ActorContext Actor,
    string? DecisionNote = null);

public sealed record WithdrawProposalCommand(
    Guid ProjectId,
    Guid ProposalId,
    ActorContext Actor,
    Guid? PipelineTaskId = null,
    Guid? SubtaskId = null,
    Guid? SessionId = null,
    string? PipelineRole = null);

public sealed record ListProposalsQuery(
    Guid ProjectId,
    ProposalStatus? Status = null,
    Guid? TaskId = null,
    int Limit = 100);

public sealed record ProposalDto(
    Guid Id,
    Guid ProjectId,
    Guid? TaskId,
    Guid? SubtaskId,
    Guid? SessionId,
    ContentTargetKind TargetKind,
    Guid TargetId,
    ProposalOperation Operation,
    int BaseRevision,
    string BaseHash,
    string PayloadJson,
    string RationaleMarkdown,
    ProposalStatus Status,
    string AuthorKind,
    Guid? AuthorUserId,
    Guid? AuthorTokenId,
    string? AuthorProfileKey,
    string? AuthorRole,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? DecidedAt,
    Guid? DecidedByUserId,
    string? DecisionNote,
    string? AcceptedPayloadJson,
    int? AppliedRevision,
    int? ConflictRevision,
    string? ConflictReason,
    Guid? SupersededById,
    int StateRevision,
    Guid? ApplyCommandId = null);

/// <summary>
/// Creates revision-bound proposals. Agent submissions are accepted only from a live Actor
/// pipeline session whose persisted subtask profile has the knowledge-proposal grant.
/// </summary>
public sealed class CreateProposalHandler : ICommandHandler<CreateProposalCommand, Result<ProposalDto>>
{
    private static readonly Regex SecretPattern = new(
        @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{24,}|bcn_[A-Za-z0-9_-]{24,}|bcd_[A-Za-z0-9_-]{24,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuditWriter _auditWriter;
    private readonly ContentWriter _contentWriter;
    private readonly TimeProvider _timeProvider;

    public CreateProposalHandler(
        IBeaconDbFactory dbFactory,
        IAuditWriter auditWriter,
        TimeProvider? timeProvider = null,
        ContentWriter? contentWriter = null)
    {
        _dbFactory = dbFactory;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _contentWriter = contentWriter ?? new ContentWriter();
    }

    public async Task<Result<ProposalDto>> HandleAsync(CreateProposalCommand command, CancellationToken ct = default)
    {
        if (command.ProjectId == Guid.Empty || command.Request.TargetId == Guid.Empty)
            return Result.Failure<ProposalDto>("ProjectId and TargetId are required.");
        if (command.Request.Operation != ProposalOperation.Update)
            return Result.Failure<ProposalDto>("Only Update proposals are currently supported.");
        var normalizedIdempotencyKey = command.Request.IdempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedIdempotencyKey)
            || normalizedIdempotencyKey.Length > Proposal.MaxIdempotencyKeyLength
            || normalizedIdempotencyKey.Any(char.IsControl))
            return Result.Failure<ProposalDto>("IdempotencyKey must contain 1-128 printable characters.");
        var normalizedRationale = command.Request.RationaleMarkdown?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRationale)
            || normalizedRationale.Length > Proposal.MaxRationaleLength)
            return Result.Failure<ProposalDto>("RationaleMarkdown must contain 1-1000 characters.");
        if (!Enum.IsDefined(command.Request.TargetKind))
            return Result.Failure<ProposalDto>("Unknown proposal target kind.");
        if (command.Request.TargetKind != ContentTargetKind.RepoPatch
            && !ProposalContentValidation.TryValidatePayload(command.Request.TargetKind, command.Request.PayloadJson, out var validationError))
            return Result.Failure<ProposalDto>(validationError!);
        if (SecretPattern.IsMatch(command.Request.PayloadJson)
            || SecretPattern.IsMatch(command.Request.RationaleMarkdown ?? string.Empty))
            return Result.Failure<ProposalDto>("Proposal content appears to contain a credential or private key. Remove secrets before submitting.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateDbContext();

        if (command.Actor.IsHuman
            && !await ProposalProjectAuthorization.CanWriteAsync(db, command.Actor, command.ProjectId, ct))
            return Result.Forbidden<ProposalDto>("Project membership is required to create a proposal.");

        if (!command.Actor.IsHuman && !command.Actor.IsApiToken)
            return Result.Forbidden<ProposalDto>();
        if (command.Actor.IsApiToken
            && !command.Actor.Capabilities.HasFlag(ApiTokenCapability.ContextRead))
            return Result.Forbidden<ProposalDto>("The API token requires ContextRead capability to submit proposals.");

        if (command.Actor.IsApiToken && command.Actor.ProjectId != command.ProjectId)
            return Result.Forbidden<ProposalDto>("The API token is not bound to this project.");

        var author = await ResolveAuthorAsync(db, command, ct);
        if (!author.Success)
            return Result.Forbidden<ProposalDto>(author.Error ?? "The proposal author is not authorized.");

        if (command.Request.TargetKind == ContentTargetKind.RepoPatch)
        {
            if (command.Actor.IsHuman || command.Request.TargetId != command.ProjectId
                || !string.Equals(author.ProfileKey, "docs-writer", StringComparison.OrdinalIgnoreCase))
                return Result.Forbidden<ProposalDto>("RepoPatch is restricted to the project-bound docs-writer Actor profile.");

            var subtask = await db.Subtasks.FirstOrDefaultAsync(item =>
                item.Id == author.SubtaskId && item.TaskId == author.TaskId
                && item.ProjectId == command.ProjectId, ct);
            if (subtask is null)
                return Result.Forbidden<ProposalDto>("The docs-writer subtask no longer exists.");
            var profile = BuiltInAgentProfiles.Resolve(subtask.ProfileKey, subtask.ProfileSnapshotJson);
            if (profile is null || profile.PathPolicy != AgentProfilePathPolicy.Defaults
                || profile.DefaultPaths.Count == 0
                || subtask.AllowedPaths.Any(path => !BuiltInAgentProfiles.IsWithinDefaults(path, profile.DefaultPaths)))
                return Result.Forbidden<ProposalDto>("RepoPatch requires a valid docs-writer documentation-only path grant.");
            if (!RepoPatchValidation.TryValidate(command.Request.PayloadJson,
                    subtask.AllowedPaths.Count == 0 ? profile.DefaultPaths : subtask.AllowedPaths,
                    profile.DefaultPaths, out _, out var patchError))
                return Result.Failure<ProposalDto>(patchError ?? "RepoPatch failed validation.", ErrorKind.Validation);
        }

        var existingByKey = await db.Proposals.FirstOrDefaultAsync(
            proposal => proposal.ProjectId == command.ProjectId
                && proposal.IdempotencyKey == normalizedIdempotencyKey,
            ct);
        if (existingByKey is not null)
        {
            if (SameRequest(existingByKey, command.Request with { RationaleMarkdown = normalizedRationale }, command.Actor, author.TaskId, author.SubtaskId, author.SessionId))
                return Result.Ok(Map(existingByKey));
            return Result.Failure<ProposalDto>("IdempotencyKey has already been used for a different proposal.", ErrorKind.Conflict);
        }

        ProposalContentValidation.ProposalTarget? target = null;
        string baseHash;
        if (command.Request.TargetKind == ContentTargetKind.RepoPatch)
        {
            if (command.Request.TargetId != command.ProjectId)
                return Result.Failure<ProposalDto>("RepoPatch TargetId must equal ProjectId.");
            var subtask = await db.Subtasks.FirstOrDefaultAsync(item =>
                item.Id == author.SubtaskId && item.ProjectId == command.ProjectId, ct);
            if (subtask is null)
                return Result.Forbidden<ProposalDto>("The docs-writer subtask no longer exists.");
            var profile = BuiltInAgentProfiles.Resolve(subtask.ProfileKey, subtask.ProfileSnapshotJson);
            if (profile is null)
                return Result.Forbidden<ProposalDto>("The docs-writer profile is unavailable.");
            if (!RepoPatchValidation.TryValidate(command.Request.PayloadJson,
                    subtask.AllowedPaths.Count == 0 ? profile.DefaultPaths : subtask.AllowedPaths,
                    profile.DefaultPaths, out var validatedPatch, out var patchError)
                || validatedPatch is null)
                return Result.Failure<ProposalDto>(patchError ?? "RepoPatch failed validation.", ErrorKind.Validation);
            baseHash = validatedPatch.BaseHash;
        }
        else
        {
            target = await ProposalContentValidation.LoadTargetAsync(
                db, command.ProjectId, command.Request.TargetKind, command.Request.TargetId, ct);
            if (target is null)
                return Result.Failure<ProposalDto>("The requested content target was not found.");
            if (!target.Reviewable)
                return Result.Failure<ProposalDto>("Only accepted or reviewed content can receive a new proposal.");

            var baseVersion = await db.ContentVersions.FirstOrDefaultAsync(version =>
                version.ProjectId == command.ProjectId
                && version.TargetKind == command.Request.TargetKind
                && version.TargetId == command.Request.TargetId
                && version.Revision == command.Request.BaseRevision, ct);
            if (baseVersion is null)
                return Result.Failure<ProposalDto>("The requested base revision does not exist.");

            baseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseVersion.ContentJson))).ToLowerInvariant();
        }
        var project = await db.Projects.FirstOrDefaultAsync(project => project.Id == command.ProjectId, ct);
        if (project is null)
            return Result.Failure<ProposalDto>("Project not found.");
        var projectOrgId = project.OrgId;

        // Expire overdue rows before applying pending limits. Expiration is a durable
        // state transition with its own audit event, not merely a read-time filter.
        var overdue = await db.Proposals
            .Where(proposal => proposal.ProjectId == command.ProjectId
                && proposal.Status == ProposalStatus.Pending
                && proposal.ExpiresAt <= now)
            .OrderBy(proposal => proposal.ExpiresAt)
            .Take(500)
            .ToListAsync(ct);
        if (overdue.Count > 0)
        {
            var expiryActor = new ActorContext(ActorType.Worker, null, false, null, null,
                command.ProjectId, projectOrgId, ApiTokenCapability.None);
            foreach (var item in overdue)
            {
                if (!item.Expire(now))
                    continue;
                _auditWriter.Stage(db, AuditEventFactory.ForActor(
                    expiryActor, "proposal.expired", "Proposal", item.Id.ToString("D"),
                    projectOrgId, command.ProjectId,
                    detailsJson: JsonSerializer.Serialize(new { newState = ProposalStatus.Expired.ToString() })));
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure<ProposalDto>(
                    "An overdue proposal changed while it was being expired. Retry the submission.",
                    ErrorKind.Conflict);
            }
        }

        var pendingInProject = await db.Proposals.CountAsync(
            proposal => proposal.ProjectId == command.ProjectId && proposal.Status == ProposalStatus.Pending, ct);
        if (pendingInProject >= 200)
            return Result.Failure<ProposalDto>("The project has reached the limit of 200 pending proposals.");

        if (author.TaskId is { } taskId)
        {
            var pendingForTask = await db.Proposals.CountAsync(
                proposal => proposal.ProjectId == command.ProjectId
                    && proposal.TaskId == taskId
                    && proposal.Status == ProposalStatus.Pending, ct);
            if (pendingForTask >= 50)
                return Result.Failure<ProposalDto>("The task has reached the limit of 50 pending proposals.");
        }

        Proposal proposal;
        try
        {
            proposal = Proposal.Create(
                command.ProjectId,
                author.TaskId,
                author.SubtaskId,
                author.SessionId,
                command.Request.TargetKind,
                command.Request.TargetId,
                command.Request.Operation,
                command.Request.BaseRevision,
                baseHash,
                command.Request.PayloadJson,
                normalizedRationale,
                command.Actor.IsHuman ? ProposalAuthorKind.Human : ProposalAuthorKind.Agent,
                command.Actor.IsHuman ? command.Actor.UserId : null,
                command.Actor.IsApiToken ? command.Actor.TokenId : null,
                author.ProfileKey,
                author.Role,
                normalizedIdempotencyKey,
                now);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ProposalDto>(exception.Message);
        }

        if (target is not null && target.Revision != command.Request.BaseRevision)
            proposal.MarkConflicted(target.Revision, "BaseRevisionChanged", now);

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var currentProject = await db.Projects.FirstOrDefaultAsync(
                    item => item.Id == command.ProjectId, ct);
                if (currentProject is null)
                    return Result.Failure<ProposalDto>("Project not found.");

                var projectAutoApply = proposal.Status == ProposalStatus.Pending
                    && ProposalPolicyCatalog.CanAutoApply(proposal.TargetKind, proposal.Operation)
                    && ProposalPolicyCatalog.GetMode(
                        currentProject.ProposalPoliciesJson, proposal.TargetKind, proposal.Operation)
                        == ProposalDecisionMode.Auto;
                var autoAccepted = false;
                var autoApplyQueued = false;

                if (proposal.Status == ProposalStatus.Pending && proposal.TargetKind == ContentTargetKind.RepoPatch)
                {
                    var subtask = await db.Subtasks.FirstOrDefaultAsync(item =>
                        item.Id == proposal.SubtaskId && item.ProjectId == proposal.ProjectId, ct);
                    var profile = subtask is null
                        ? null
                        : BuiltInAgentProfiles.Resolve(subtask.ProfileKey, subtask.ProfileSnapshotJson);
                    var profileAutoApply = profile is not null
                        && Enum.TryParse<AgentProfileApplyPolicy>(profile.ApplyPolicy, true, out var applyPolicy)
                        && applyPolicy == AgentProfileApplyPolicy.Auto;
                    if (projectAutoApply && profileAutoApply)
                    {
                        var queued = await RepoPatchProposalWorkflow.QueueAsync(
                            db, proposal, null, command.Actor, _auditWriter, now,
                            requireProfileAuto: true, ct);
                        if (!queued.Success)
                            return Result.Failure<ProposalDto>(queued.Error ?? "RepoPatch could not be queued.", queued.Kind);
                        autoApplyQueued = true;
                    }
                }
                else if (proposal.Status == ProposalStatus.Pending)
                {
                    var latestTarget = await ProposalContentValidation.LoadTargetAsync(
                        db, command.ProjectId, command.Request.TargetKind, command.Request.TargetId, ct);
                    if (latestTarget is null)
                        proposal.MarkConflicted(null, "TargetMissing", now);
                    else if (!latestTarget.Reviewable)
                        proposal.MarkConflicted(latestTarget.Revision, "TargetNoLongerReviewable", now);
                    else if (latestTarget.Revision != proposal.BaseRevision)
                        proposal.MarkConflicted(latestTarget.Revision, "BaseRevisionChanged", now);
                    else if (projectAutoApply)
                    {
                        var attribution = ContentAttribution.FromActor(
                            command.Actor, author.SubtaskId?.ToString("D"));
                        var changedByType = attribution.Type
                            ?? (command.Actor.IsHuman ? "Human" : "ApiToken");
                        var changedById = attribution.Id
                            ?? command.Actor.UserId?.ToString("D")
                            ?? command.Actor.TokenId?.ToString("D")
                            ?? string.Empty;

                        try
                        {
                            ProposalContentValidation.Apply(
                                latestTarget.Entity, proposal.TargetKind,
                                command.Request.PayloadJson, changedByType, changedById);
                        }
                        catch (ArgumentException exception)
                        {
                            return Result.Failure<ProposalDto>(exception.Message);
                        }
                        catch (InvalidOperationException exception)
                        {
                            return Result.Failure<ProposalDto>(exception.Message);
                        }

                        var appliedRevision = ProposalContentValidation.GetRevision(latestTarget.Entity);
                        if (appliedRevision != latestTarget.Revision)
                            _contentWriter.AddVersion(
                                db, latestTarget.Entity, changedByType, changedById,
                                proposalId: proposal.Id);

                        proposal.AutoAccept(appliedRevision, command.Request.PayloadJson, now);
                        StageAudit(db, command.Actor, proposal, projectOrgId, "proposal.auto_accepted", ProposalStatus.Accepted);
                        autoAccepted = true;
                    }
                }

                // A newer proposal from the same Actor supersedes its previous pending
                // proposal even when the new proposal was accepted by an Auto policy.
                if ((proposal.Status == ProposalStatus.Pending || autoAccepted || autoApplyQueued)
                    && author.SubtaskId is { } sourceSubtask)
                {
                    var previous = await db.Proposals
                        .Where(existing => existing.ProjectId == command.ProjectId
                            && existing.SubtaskId == sourceSubtask
                            && existing.TargetKind == command.Request.TargetKind
                            && existing.TargetId == command.Request.TargetId
                            && existing.Status == ProposalStatus.Pending)
                        .ToListAsync(ct);
                    foreach (var item in previous)
                    {
                        item.Supersede(proposal.Id, now);
                        StageAudit(db, command.Actor, item, projectOrgId, "proposal.superseded", ProposalStatus.Superseded);
                    }
                    if (previous.Count > 0)
                        await db.SaveChangesAsync(ct);
                }

                db.Proposals.Add(proposal);
                StageAudit(db, command.Actor, proposal, projectOrgId, "proposal.created", proposal.Status);
                if (proposal.Status == ProposalStatus.Conflicted)
                    StageAudit(db, command.Actor, proposal, projectOrgId, "proposal.conflicted", ProposalStatus.Conflicted);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Result.Ok(Map(proposal));
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveConcurrentSubmissionAsync(command, author, normalizedRationale, normalizedIdempotencyKey, ct);
        }
        catch (DbUpdateException)
        {
            return await ResolveConcurrentSubmissionAsync(command, author, normalizedRationale, normalizedIdempotencyKey, ct);
        }
    }

    private async Task<Result<ProposalDto>> ResolveConcurrentSubmissionAsync(
        CreateProposalCommand command,
        ProposalAuthorScope author,
        string normalizedRationale,
        string normalizedIdempotencyKey,
        CancellationToken ct)
    {
        await using var verify = _dbFactory.CreateDbContext();
        var existing = await verify.Proposals.FirstOrDefaultAsync(proposal =>
            proposal.ProjectId == command.ProjectId
            && proposal.IdempotencyKey == normalizedIdempotencyKey, ct);
        if (existing is not null
            && SameRequest(existing,
                command.Request with { RationaleMarkdown = normalizedRationale },
                command.Actor,
                author.TaskId,
                author.SubtaskId,
                author.SessionId))
            return Result.Ok(Map(existing));

        return Result.Failure<ProposalDto>(
            "The proposal could not be saved because another request changed the same proposal scope. Refresh and retry.",
            ErrorKind.Conflict);
    }

    private async Task<ProposalAuthorScope> ResolveAuthorAsync(
        IBeaconDb db,
        CreateProposalCommand command,
        CancellationToken ct)
    {
        if (command.Actor.IsHuman)
        {
            if (command.Actor.UserId is null || command.SubtaskId is not null || command.SessionId is not null)
                return ProposalAuthorScope.Forbidden("Human proposals cannot impersonate a pipeline session.");
            return ProposalAuthorScope.Human;
        }

        if (!command.Actor.IsApiToken
            || command.Actor.TokenId is null
            || command.PipelineRole != PipelineRole.Actor.ToString()
            || command.PipelineTaskId is null
            || command.SubtaskId is null
            || command.SessionId is null)
            return ProposalAuthorScope.Forbidden("An agent proposal must originate from a task-bound Actor session.");

        var subtask = await db.Subtasks.FirstOrDefaultAsync(item =>
            item.Id == command.SubtaskId.Value
            && item.TaskId == command.PipelineTaskId.Value
            && item.ProjectId == command.ProjectId
            && item.Status == SubtaskStatus.InProgress, ct);
        if (subtask is null
            || subtask.ProfileKey is not ("knowledge-curator" or "docs-writer")
            || !subtask.AllowedMcpTools.Contains(McpToolNames.KnowledgePropose, StringComparer.Ordinal))
            return ProposalAuthorScope.Forbidden("The active subtask profile is not granted knowledge proposal access.");

        if (string.Equals(subtask.ProfileKey, "docs-writer", StringComparison.OrdinalIgnoreCase))
        {
            var profile = BuiltInAgentProfiles.Resolve(subtask.ProfileKey, subtask.ProfileSnapshotJson);
            if (profile is null || profile.PathPolicy != AgentProfilePathPolicy.Defaults
                || profile.DefaultPaths.Count == 0)
                return ProposalAuthorScope.Forbidden("The docs-writer profile has no safe documentation path grant.");
            var paths = subtask.AllowedPaths.Count == 0 ? profile.DefaultPaths : subtask.AllowedPaths;
            if (paths.Any(path => !BuiltInAgentProfiles.IsWithinDefaults(path, profile.DefaultPaths)))
                return ProposalAuthorScope.Forbidden("The docs-writer subtask exceeds its documentation-only path grant.");
        }

        var session = await db.PipelineSessions.FirstOrDefaultAsync(item =>
            item.Id == command.SessionId.Value
            && item.ProjectId == command.ProjectId
            && item.TaskId == command.PipelineTaskId.Value
            && item.SubtaskId == command.SubtaskId.Value
            && item.Role == PipelineRole.Actor
            && item.Status == SessionStatus.Active, ct);
        if (session is null)
            return ProposalAuthorScope.Forbidden("The Actor pipeline session is no longer active.");

        var taskExists = await db.Tasks.AnyAsync(task =>
            task.Id == command.PipelineTaskId.Value && task.ProjectId == command.ProjectId, ct);
        if (!taskExists)
            return ProposalAuthorScope.Forbidden("The Actor task does not belong to this project.");

        return new ProposalAuthorScope(true, command.PipelineTaskId, command.SubtaskId, command.SessionId,
            subtask.ProfileKey, PipelineRole.Actor.ToString(), null);
    }

    private static bool SameRequest(
        Proposal existing,
        CreateProposalRequest request,
        ActorContext actor,
        Guid? taskId,
        Guid? subtaskId,
        Guid? sessionId) =>
        existing.TargetKind == request.TargetKind
        && existing.TargetId == request.TargetId
        && existing.Operation == request.Operation
        && existing.BaseRevision == request.BaseRevision
        && existing.PayloadJson == request.PayloadJson
        && existing.RationaleMarkdown == request.RationaleMarkdown
        && existing.AuthorKind == (actor.IsHuman ? ProposalAuthorKind.Human : ProposalAuthorKind.Agent)
        && existing.AuthorUserId == (actor.IsHuman ? actor.UserId : null)
        && existing.AuthorTokenId == (actor.IsApiToken ? actor.TokenId : null)
        && existing.TaskId == taskId
        && existing.SubtaskId == subtaskId
        && existing.SessionId == sessionId;

    private void StageAudit(
        IBeaconDb db,
        ActorContext actor,
        Proposal proposal,
        Guid orgId,
        string action,
        ProposalStatus status)
    {
        var audit = AuditEventFactory.ForActor(
            actor,
            action,
            "Proposal",
            proposal.Id.ToString("D"),
            orgId,
            proposal.ProjectId,
            detailsJson: JsonSerializer.Serialize(new { newState = status.ToString() }));
        _auditWriter.Stage(db, audit);
    }

    private sealed record ProposalAuthorScope(
        bool Success,
        Guid? TaskId,
        Guid? SubtaskId,
        Guid? SessionId,
        string? ProfileKey,
        string? Role,
        string? Error)
    {
        public static ProposalAuthorScope Human => new(true, null, null, null, null, "Human", null);
        public static ProposalAuthorScope Forbidden(string reason) => new(false, null, null, null, null, null, reason);
    }

    private static ProposalDto Map(Proposal proposal) => ProposalMapper.Map(proposal);
}

/// <summary>Human-only decisions over a proposal Inbox.</summary>
public sealed class AcceptProposalHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;
    private readonly ContentWriter _contentWriter;
    private readonly TimeProvider _timeProvider;
    private readonly PipelineActorQueueDispatcher? _plannerDispatcher;

    public AcceptProposalHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter auditWriter,
        ContentWriter contentWriter,
        TimeProvider? timeProvider = null,
        PipelineActorQueueDispatcher? plannerDispatcher = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
        _contentWriter = contentWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _plannerDispatcher = plannerDispatcher;
    }

    public async Task<Result<ProposalDto>> HandleAsync(AcceptProposalCommand command, CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<ProposalDto>("Only a human can accept a proposal.");

        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        var decisionTaskId = (Guid?)null;
        var decisionCommitted = false;
        try
        {
            var result = await strategy.ExecuteAsync(async () =>
            {
                // Reset captured state on every execution-strategy retry. Only a newly
                // committed transition may trigger a Planner wake-up.
                decisionTaskId = null;
                decisionCommitted = false;
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var proposal = await db.Proposals.FirstOrDefaultAsync(
                    item => item.Id == command.ProposalId && item.ProjectId == command.ProjectId, ct);
                if (proposal is null)
                    return Result.Failure<ProposalDto>("Proposal not found.");
                if (!await ProposalProjectAuthorization.CanWriteAsync(db, command.Actor, proposal.ProjectId, ct))
                    return Result.Forbidden<ProposalDto>("Project membership is required to accept a proposal.");

                if (proposal.TargetKind is ContentTargetKind.Decision or ContentTargetKind.Constraint)
                {
                    var governance = await _authorization.CanAsync(
                        db, command.Actor, ResourceType.Project, AuthAction.Administer,
                        projectId: proposal.ProjectId, ct: ct);
                    if (!governance.Success)
                        return Result.Forbidden<ProposalDto>("Only a project or organization Owner/Admin can accept Decision or Constraint proposals.");
                }

                if (proposal.Status == ProposalStatus.Accepted)
                {
                    await transaction.CommitAsync(ct);
                    return Result.Ok(ProposalMapper.Map(proposal));
                }
                if (proposal.Status == ProposalStatus.Applying
                    && proposal.TargetKind == ContentTargetKind.RepoPatch
                    && proposal.ApplyCommandId is { } applyCommandId)
                {
                    var existingCommand = await db.WorkstationCommands.IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(item => item.Id == applyCommandId
                            && item.ProjectId == proposal.ProjectId
                            && item.Kind == WorkstationCommandKind.ApplyProposalPatch
                            && (item.Status == WorkstationCommandStatus.Pending
                                || item.Status == WorkstationCommandStatus.Running), ct);
                    if (existingCommand is null)
                        return Result.Failure<ProposalDto>(
                            "The RepoPatch apply command is no longer pending or running. Refresh the proposal state.",
                            ErrorKind.Conflict);

                    // The first approval and command were committed in one transaction.
                    // A retried approval must return that same Applying state, not enqueue
                    // a second workstation command or report a false rejection.
                    await transaction.CommitAsync(ct);
                    return Result.Ok(ProposalMapper.Map(proposal));
                }
                if (proposal.Status != ProposalStatus.Pending)
                    return Result.Failure<ProposalDto>($"Proposal cannot be accepted in status {proposal.Status}.");

                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var orgId = await GetOrgIdAsync(db, proposal.ProjectId, ct);
                if (orgId is null)
                    return Result.Failure<ProposalDto>("Project not found.");

                if (proposal.Expire(now))
                {
                    StageAudit(db, command.Actor, proposal, orgId.Value, "proposal.expired", ProposalStatus.Expired);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return Result.Failure<ProposalDto>("Proposal has expired.");
                }

                if (proposal.TargetKind == ContentTargetKind.RepoPatch)
                {
                    if (command.AcceptedPayloadJson is not null
                        && !string.Equals(command.AcceptedPayloadJson, proposal.PayloadJson, StringComparison.Ordinal))
                        return Result.Failure<ProposalDto>(
                            "RepoPatch payloads are immutable during approval. Reject and resubmit a corrected patch.",
                            ErrorKind.Validation);

                    var queued = await RepoPatchProposalWorkflow.QueueAsync(
                        db, proposal, command.Actor.UserId, command.Actor, _auditWriter, now,
                        requireProfileAuto: false, ct);
                    if (!queued.Success)
                        return Result.Failure<ProposalDto>(
                            queued.Error ?? "RepoPatch could not be queued.",
                            queued.Kind);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return Result.Ok(ProposalMapper.Map(proposal));
                }

                var payload = command.AcceptedPayloadJson ?? proposal.PayloadJson;
                if (!ProposalContentValidation.TryValidatePayload(proposal.TargetKind, payload, out var validationError))
                    return Result.Failure<ProposalDto>(validationError!);

                var target = await ProposalContentValidation.LoadTargetAsync(
                    db, proposal.ProjectId, proposal.TargetKind, proposal.TargetId, ct);
                if (target is null)
                    return await MarkConflictAsync(db, transaction, proposal, command.Actor, orgId.Value,
                        null, "TargetMissing", now, ct);
                if (!target.Reviewable)
                    return await MarkConflictAsync(db, transaction, proposal, command.Actor, orgId.Value,
                        target.Revision, "TargetNoLongerReviewable", now, ct);
                if (target.Revision != proposal.BaseRevision)
                    return await MarkConflictAsync(db, transaction, proposal, command.Actor, orgId.Value,
                        target.Revision, "BaseRevisionChanged", now, ct);

                var baseVersion = await db.ContentVersions.FirstOrDefaultAsync(version =>
                    version.ProjectId == proposal.ProjectId
                    && version.TargetKind == proposal.TargetKind
                    && version.TargetId == proposal.TargetId
                    && version.Revision == proposal.BaseRevision, ct);
                if (baseVersion is null)
                    return await MarkConflictAsync(db, transaction, proposal, command.Actor, orgId.Value,
                        target.Revision, "BaseSnapshotMissing", now, ct);

                var currentBaseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseVersion.ContentJson))).ToLowerInvariant();
                if (!string.Equals(currentBaseHash, proposal.BaseHash, StringComparison.Ordinal))
                    return await MarkConflictAsync(db, transaction, proposal, command.Actor, orgId.Value,
                        target.Revision, "BaseSnapshotChanged", now, ct);

                var attribution = ContentAttribution.FromActor(command.Actor);
                var changedByType = attribution.Type ?? "Human";
                var changedById = attribution.Id ?? command.Actor.UserId.Value.ToString("D");
                try
                {
                    ProposalContentValidation.Apply(
                        target.Entity, proposal.TargetKind, payload, changedByType, changedById);
                }
                catch (ArgumentException exception)
                {
                    return Result.Failure<ProposalDto>(exception.Message);
                }
                catch (InvalidOperationException exception)
                {
                    return Result.Failure<ProposalDto>(exception.Message);
                }

                var appliedRevision = ProposalContentValidation.GetRevision(target.Entity);
                if (appliedRevision != target.Revision)
                    _contentWriter.AddVersion(db, target.Entity, changedByType, changedById,
                        proposalId: proposal.Id);

                proposal.Accept(command.Actor.UserId.Value, appliedRevision,
                    command.AcceptedPayloadJson, now);
                StageAudit(db, command.Actor, proposal, orgId.Value, "proposal.accepted", ProposalStatus.Accepted);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                decisionTaskId = proposal.TaskId;
                decisionCommitted = true;
                return Result.Ok(ProposalMapper.Map(proposal));
            });

            if (decisionCommitted && decisionTaskId is { } taskId && _plannerDispatcher is not null)
                await _plannerDispatcher.DispatchNextAsync(taskId, ct);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await using var verify = _dbFactory.CreateDbContext();
            var accepted = await verify.Proposals.FirstOrDefaultAsync(
                item => item.Id == command.ProposalId && item.ProjectId == command.ProjectId, ct);
            if (accepted?.Status == ProposalStatus.Accepted)
                return Result.Ok(ProposalMapper.Map(accepted));
            return Result.Failure<ProposalDto>("The proposal or its target changed while it was being accepted. Refresh and retry.", ErrorKind.Conflict);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<ProposalDto>("The proposal could not be applied atomically. No change was committed.", ErrorKind.Conflict);
        }
    }

    private async Task<Result<ProposalDto>> MarkConflictAsync(
        IBeaconDb db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        Proposal proposal,
        ActorContext actor,
        Guid orgId,
        int? actualRevision,
        string reason,
        DateTime now,
        CancellationToken ct)
    {
        proposal.MarkConflicted(actualRevision, reason, now);
        StageAudit(db, actor, proposal, orgId, "proposal.conflicted", ProposalStatus.Conflicted);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Failure<ProposalDto>(
            $"Proposal is conflicted ({reason}); current revision is {actualRevision?.ToString() ?? "missing"}.",
            ErrorKind.Conflict);
    }

    private void StageAudit(IBeaconDb db, ActorContext actor, Proposal proposal, Guid orgId, string action, ProposalStatus status)
    {
        var audit = AuditEventFactory.ForActor(actor, action, "Proposal", proposal.Id.ToString("D"),
            orgId, proposal.ProjectId,
            detailsJson: JsonSerializer.Serialize(new { newState = status.ToString() }));
        _auditWriter.Stage(db, audit);
    }

    internal static async Task<Guid?> GetOrgIdAsync(IBeaconDb db, Guid projectId, CancellationToken ct) =>
        await db.Projects.Where(project => project.Id == projectId)
            .Select(project => (Guid?)project.OrgId).FirstOrDefaultAsync(ct);
}

/// <summary>Rejects a proposal. Decision/Constraint governance proposals require Owner/Admin.</summary>
public sealed class RejectProposalHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;
    private readonly PipelineActorQueueDispatcher? _plannerDispatcher;

    public RejectProposalHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter auditWriter,
        TimeProvider? timeProvider = null,
        PipelineActorQueueDispatcher? plannerDispatcher = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _plannerDispatcher = plannerDispatcher;
    }

    public async Task<Result<ProposalDto>> HandleAsync(RejectProposalCommand command, CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<ProposalDto>("Only a human can reject a proposal.");
        await using var db = _dbFactory.CreateDbContext();
        var proposal = await db.Proposals.FirstOrDefaultAsync(
            item => item.Id == command.ProposalId && item.ProjectId == command.ProjectId, ct);
        if (proposal is null)
            return Result.Failure<ProposalDto>("Proposal not found.");
        if (!await ProposalProjectAuthorization.CanWriteAsync(db, command.Actor, proposal.ProjectId, ct))
            return Result.Forbidden<ProposalDto>("Project membership is required to reject a proposal.");
        if (proposal.TargetKind is ContentTargetKind.Decision or ContentTargetKind.Constraint)
        {
            var governance = await _authorization.CanAsync(
                db, command.Actor, ResourceType.Project, AuthAction.Administer,
                projectId: proposal.ProjectId, ct: ct);
            if (!governance.Success)
                return Result.Forbidden<ProposalDto>("Only a project or organization Owner/Admin can reject Decision or Constraint proposals.");
        }

        if (proposal.Status == ProposalStatus.Rejected)
            return Result.Ok(ProposalMapper.Map(proposal));
        if (proposal.Status != ProposalStatus.Pending)
            return Result.Failure<ProposalDto>($"Proposal cannot be rejected in status {proposal.Status}.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (proposal.Expire(now))
        {
            var expiredOrgId = await AcceptProposalHandler.GetOrgIdAsync(db, proposal.ProjectId, ct);
            if (expiredOrgId is null)
                return Result.Failure<ProposalDto>("Project not found.");
            StageAudit(db, command.Actor, proposal, expiredOrgId.Value, "proposal.expired", ProposalStatus.Expired);
            await db.SaveChangesAsync(ct);
            return Result.Failure<ProposalDto>("Proposal has expired.");
        }

        var orgId = await AcceptProposalHandler.GetOrgIdAsync(db, proposal.ProjectId, ct);
        if (orgId is null)
            return Result.Failure<ProposalDto>("Project not found.");
        try
        {
            proposal.Reject(command.Actor.UserId.Value, command.DecisionNote, now);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ProposalDto>(exception.Message);
        }

        StageAudit(db, command.Actor, proposal, orgId.Value, "proposal.rejected", ProposalStatus.Rejected);
        try
        {
            await db.SaveChangesAsync(ct);
            if (proposal.TaskId is { } taskId && _plannerDispatcher is not null)
                await _plannerDispatcher.DispatchNextAsync(taskId, ct);
            return Result.Ok(ProposalMapper.Map(proposal));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ProposalDto>("The proposal changed while it was being rejected. Refresh and retry.", ErrorKind.Conflict);
        }
    }

    private void StageAudit(IBeaconDb db, ActorContext actor, Proposal proposal, Guid orgId, string action, ProposalStatus status)
    {
        var audit = AuditEventFactory.ForActor(actor, action, "Proposal", proposal.Id.ToString("D"),
            orgId, proposal.ProjectId,
            detailsJson: JsonSerializer.Serialize(new { newState = status.ToString() }));
        _auditWriter.Stage(db, audit);
    }
}

/// <summary>Allows the original human author or the same live Actor subtask to withdraw a pending proposal.</summary>
public sealed class WithdrawProposalHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public WithdrawProposalHandler(IBeaconDbFactory dbFactory, IAuditWriter auditWriter, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<ProposalDto>> HandleAsync(WithdrawProposalCommand command, CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman && !command.Actor.IsApiToken)
            return Result.Forbidden<ProposalDto>();
        if (command.Actor.IsApiToken
            && !command.Actor.Capabilities.HasFlag(ApiTokenCapability.ContextRead))
            return Result.Forbidden<ProposalDto>("The API token requires ContextRead capability to withdraw proposals.");

        await using var db = _dbFactory.CreateDbContext();
        var proposal = await db.Proposals.FirstOrDefaultAsync(
            item => item.Id == command.ProposalId && item.ProjectId == command.ProjectId, ct);
        if (proposal is null)
            return Result.Failure<ProposalDto>("Proposal not found.");

        var isOriginalHuman = command.Actor.IsHuman
            && command.Actor.UserId is { } userId
            && proposal.AuthorKind == ProposalAuthorKind.Human
            && proposal.AuthorUserId == userId;
        var isOriginalAgent = command.Actor.IsApiToken
            && command.Actor.TokenId is { } tokenId
            && proposal.AuthorKind == ProposalAuthorKind.Agent
            && proposal.AuthorTokenId == tokenId
            && proposal.TaskId == command.PipelineTaskId
            && proposal.SubtaskId == command.SubtaskId
            && proposal.SessionId == command.SessionId
            && string.Equals(command.PipelineRole, PipelineRole.Actor.ToString(), StringComparison.Ordinal);
        if (!isOriginalHuman && !isOriginalAgent)
            return Result.Forbidden<ProposalDto>("Only the original author can withdraw this proposal.");
        if (isOriginalHuman
            && !await ProposalProjectAuthorization.CanWriteAsync(db, command.Actor, proposal.ProjectId, ct))
            return Result.Forbidden<ProposalDto>("Project membership is required to withdraw this proposal.");
        if (isOriginalAgent)
        {
            if (command.Actor.ProjectId != command.ProjectId)
                return Result.Forbidden<ProposalDto>("The API token is not bound to this project.");

            var subtask = await db.Subtasks.FirstOrDefaultAsync(item =>
                item.Id == command.SubtaskId
                && item.TaskId == command.PipelineTaskId
                && item.ProjectId == command.ProjectId
                && item.Status == SubtaskStatus.InProgress, ct);
            if (subtask is null
                || subtask.ProfileKey is not ("knowledge-curator" or "docs-writer")
                || !subtask.AllowedMcpTools.Contains(McpToolNames.KnowledgeWithdraw, StringComparer.Ordinal))
                return Result.Forbidden<ProposalDto>("The original Actor subtask is no longer active or authorized.");

            var sessionIsActive = await db.PipelineSessions.AnyAsync(item =>
                item.Id == command.SessionId
                && item.ProjectId == command.ProjectId
                && item.TaskId == command.PipelineTaskId
                && item.SubtaskId == command.SubtaskId
                && item.Role == PipelineRole.Actor
                && item.Status == SessionStatus.Active, ct);
            if (!sessionIsActive)
                return Result.Forbidden<ProposalDto>("The original Actor pipeline session is no longer active.");
        }

        if (proposal.Status == ProposalStatus.Withdrawn)
            return Result.Ok(ProposalMapper.Map(proposal));
        if (proposal.Status != ProposalStatus.Pending)
            return Result.Failure<ProposalDto>($"Proposal cannot be withdrawn in status {proposal.Status}.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (proposal.Expire(now))
        {
            var expiryOrg = await AcceptProposalHandler.GetOrgIdAsync(db, proposal.ProjectId, ct);
            if (expiryOrg is null)
                return Result.Failure<ProposalDto>("Project not found.");
            StageAudit(db, command.Actor, proposal, expiryOrg.Value, "proposal.expired", ProposalStatus.Expired);
            await db.SaveChangesAsync(ct);
            return Result.Failure<ProposalDto>("Proposal has expired.");
        }

        var orgId = await AcceptProposalHandler.GetOrgIdAsync(db, proposal.ProjectId, ct);
        if (orgId is null)
            return Result.Failure<ProposalDto>("Project not found.");
        proposal.Withdraw(now);
        StageAudit(db, command.Actor, proposal, orgId.Value, "proposal.withdrawn", ProposalStatus.Withdrawn);
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Ok(ProposalMapper.Map(proposal));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ProposalDto>("The proposal changed while it was being withdrawn. Refresh and retry.", ErrorKind.Conflict);
        }
    }

    private void StageAudit(IBeaconDb db, ActorContext actor, Proposal proposal, Guid orgId, string action, ProposalStatus status)
    {
        var audit = AuditEventFactory.ForActor(actor, action, "Proposal", proposal.Id.ToString("D"),
            orgId, proposal.ProjectId,
            detailsJson: JsonSerializer.Serialize(new { newState = status.ToString() }));
        _auditWriter.Stage(db, audit);
    }
}

/// <summary>Lists project proposals and materializes elapsed 30-day expirations.</summary>
public sealed class ListProposalsHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public ListProposalsHandler(IBeaconDbFactory dbFactory, IAuditWriter auditWriter, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<IReadOnlyList<ProposalDto>>> HandleAsync(
        ListProposalsQuery query,
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<IReadOnlyList<ProposalDto>>("The proposal Inbox is human-only.");

        await using var db = _dbFactory.CreateDbContext();
        if (!await ProposalProjectAuthorization.CanWriteAsync(db, actor, query.ProjectId, ct))
            return Result.Forbidden<IReadOnlyList<ProposalDto>>("Project membership is required to view proposals.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var orgId = await AcceptProposalHandler.GetOrgIdAsync(db, query.ProjectId, ct);
        if (orgId is null)
            return Result.Failure<IReadOnlyList<ProposalDto>>("Project not found.");

        var expired = await db.Proposals
            .Where(proposal => proposal.ProjectId == query.ProjectId
                && proposal.Status == ProposalStatus.Pending
                && proposal.ExpiresAt <= now)
            .OrderBy(proposal => proposal.ExpiresAt)
            .Take(500)
            .ToListAsync(ct);
        if (expired.Count > 0)
        {
            var systemActor = new ActorContext(ActorType.Worker, null, false, null, null,
                query.ProjectId, orgId, ApiTokenCapability.None);
            foreach (var proposal in expired)
            {
                if (!proposal.Expire(now))
                    continue;
                var audit = AuditEventFactory.ForActor(systemActor, "proposal.expired", "Proposal",
                    proposal.Id.ToString("D"), orgId, query.ProjectId,
                    detailsJson: JsonSerializer.Serialize(new { newState = ProposalStatus.Expired.ToString() }));
                _auditWriter.Stage(db, audit);
            }
            await db.SaveChangesAsync(ct);
        }

        var items = await db.Proposals
            .Where(proposal => proposal.ProjectId == query.ProjectId
                && (query.Status == null || proposal.Status == query.Status)
                && (query.TaskId == null || proposal.TaskId == query.TaskId))
            .OrderByDescending(proposal => proposal.CreatedAt)
            .Take(Math.Clamp(query.Limit, 1, 100))
            .Select(proposal => new ProposalDto(
                proposal.Id, proposal.ProjectId, proposal.TaskId, proposal.SubtaskId, proposal.SessionId,
                proposal.TargetKind, proposal.TargetId, proposal.Operation, proposal.BaseRevision, proposal.BaseHash,
                proposal.PayloadJson, proposal.RationaleMarkdown, proposal.Status, proposal.AuthorKind.ToString(),
                proposal.AuthorUserId, proposal.AuthorTokenId, proposal.AuthorProfileKey, proposal.AuthorRole,
                proposal.CreatedAt, proposal.ExpiresAt, proposal.DecidedAt, proposal.DecidedByUserId,
                proposal.DecisionNote, proposal.AcceptedPayloadJson, proposal.AppliedRevision,
                proposal.ConflictRevision, proposal.ConflictReason, proposal.SupersededById, proposal.StateRevision, proposal.ApplyCommandId))
            .ToListAsync(ct);
        return Result.Ok((IReadOnlyList<ProposalDto>)items);
    }
}

/// <summary>Minimal status view of proposals authored by the current Actor subtask.</summary>
public sealed record OwnProposalDto(
    Guid Id,
    ContentTargetKind TargetKind,
    Guid TargetId,
    ProposalOperation Operation,
    ProposalStatus Status,
    int BaseRevision,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? DecidedAt,
    string? DecisionNote,
    int? AppliedRevision,
    int? ConflictRevision,
    string? ConflictReason,
    Guid? SupersededById,
    int StateRevision);

/// <summary>Lists only proposals belonging to the current live Actor subtask.</summary>
public sealed class ListOwnProposalsHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public ListOwnProposalsHandler(
        IBeaconDbFactory dbFactory,
        IAuditWriter auditWriter,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<IReadOnlyList<OwnProposalDto>>> HandleAsync(
        Guid projectId,
        ActorContext actor,
        Guid? taskId,
        Guid? subtaskId,
        Guid? sessionId,
        string? pipelineRole,
        ProposalStatus? status = null,
        CancellationToken ct = default)
    {
        if (!actor.IsApiToken
            || actor.TokenId is not { } tokenId
            || actor.ProjectId != projectId
            || !actor.Capabilities.HasFlag(ApiTokenCapability.ContextRead)
            || taskId is not { } scopedTaskId
            || subtaskId is not { } scopedSubtaskId
            || sessionId is not { } scopedSessionId
            || !string.Equals(pipelineRole, PipelineRole.Actor.ToString(), StringComparison.Ordinal))
            return Result.Forbidden<IReadOnlyList<OwnProposalDto>>("A live, project-bound Actor session with ContextRead is required.");

        await using var db = _dbFactory.CreateDbContext();
        var subtask = await db.Subtasks.FirstOrDefaultAsync(item =>
            item.Id == scopedSubtaskId
            && item.TaskId == scopedTaskId
            && item.ProjectId == projectId
            && item.Status == SubtaskStatus.InProgress, ct);
        if (subtask is null
            || subtask.ProfileKey is not ("knowledge-curator" or "docs-writer")
            || !subtask.AllowedMcpTools.Contains(McpToolNames.KnowledgeProposals, StringComparer.Ordinal))
            return Result.Forbidden<IReadOnlyList<OwnProposalDto>>("This Actor profile is not granted knowledge_proposals.");

        var activeSession = await db.PipelineSessions.AnyAsync(item =>
            item.Id == scopedSessionId
            && item.ProjectId == projectId
            && item.TaskId == scopedTaskId
            && item.SubtaskId == scopedSubtaskId
            && item.Role == PipelineRole.Actor
            && item.Status == SessionStatus.Active, ct);
        if (!activeSession)
            return Result.Forbidden<IReadOnlyList<OwnProposalDto>>("The Actor pipeline session is no longer active.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var items = await db.Proposals
            .Where(proposal => proposal.ProjectId == projectId
                && proposal.TaskId == scopedTaskId
                && proposal.SubtaskId == scopedSubtaskId
                && proposal.SessionId == scopedSessionId
                && proposal.AuthorKind == ProposalAuthorKind.Agent
                && proposal.AuthorTokenId == tokenId)
            .OrderByDescending(proposal => proposal.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        var expired = items.Where(proposal => proposal.Status == ProposalStatus.Pending
            && proposal.ExpiresAt <= now).ToArray();
        if (expired.Length > 0)
        {
            var orgId = await AcceptProposalHandler.GetOrgIdAsync(db, projectId, ct);
            if (orgId is null)
                return Result.Failure<IReadOnlyList<OwnProposalDto>>("Project not found.");
            var systemActor = new ActorContext(
                ActorType.Worker, null, false, null, null, projectId, orgId, ApiTokenCapability.None);
            foreach (var proposal in expired)
            {
                if (!proposal.Expire(now))
                    continue;
                _auditWriter.Stage(db, AuditEventFactory.ForActor(
                    systemActor, "proposal.expired", "Proposal", proposal.Id.ToString("D"),
                    orgId, projectId,
                    detailsJson: JsonSerializer.Serialize(new { newState = ProposalStatus.Expired.ToString() })));
            }
            await db.SaveChangesAsync(ct);
        }

        var result = items
            .Where(proposal => status is null || proposal.Status == status)
            .Select(proposal => new OwnProposalDto(
                proposal.Id, proposal.TargetKind, proposal.TargetId, proposal.Operation,
                proposal.Status, proposal.BaseRevision, proposal.CreatedAt, proposal.ExpiresAt,
                proposal.DecidedAt, proposal.DecisionNote, proposal.AppliedRevision,
                proposal.ConflictRevision, proposal.ConflictReason, proposal.SupersededById,
                proposal.StateRevision))
            .ToArray();
        return Result.Ok<IReadOnlyList<OwnProposalDto>>(result);
    }
}

internal static class ProposalProjectAuthorization
{
    /// <summary>
    /// Human writes require project membership or inherited Owner/Admin rights. Agent proposals
    /// require a project-bound ContextRead token; the HTTP harness independently enforces the
    /// live Actor session and per-profile MCP grant before reaching these handlers.
    /// </summary>
    public static async Task<bool> CanSubmitAsync(
        IBeaconDb db,
        ActorContext actor,
        Guid projectId,
        CancellationToken ct)
    {
        if (actor.IsHuman)
            return await CanWriteAsync(db, actor, projectId, ct);

        return actor.IsApiToken
            && actor.ProjectId == projectId
            && actor.Capabilities.HasFlag(ApiTokenCapability.ContextRead);
    }

    /// <summary>
    /// Ordinary proposal access follows project membership. Org Owner/Admin and system
    /// administrators inherit access; an org Member without project membership does not.
    /// </summary>
    public static async Task<bool> CanWriteAsync(
        IBeaconDb db,
        ActorContext actor,
        Guid projectId,
        CancellationToken ct)
    {
        if (!actor.IsHuman || actor.UserId is not { } userId)
            return false;
        if (actor.IsAdmin)
            return true;

        var orgId = await db.Projects.IgnoreQueryFilters()
            .Where(project => project.Id == projectId)
            .Select(project => (Guid?)project.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is null)
            return false;

        var isProjectMember = await db.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(member => member.ProjectId == projectId && member.UserId == userId, ct);
        if (isProjectMember)
            return true;

        return await db.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(member => member.OrgId == orgId.Value
                && member.UserId == userId
                && (member.Role == MemberRole.Owner || member.Role == MemberRole.Admin), ct);
    }
}

internal static class ProposalMapper
{
    public static ProposalDto Map(Proposal proposal) => new(
        proposal.Id, proposal.ProjectId, proposal.TaskId, proposal.SubtaskId, proposal.SessionId,
        proposal.TargetKind, proposal.TargetId, proposal.Operation, proposal.BaseRevision, proposal.BaseHash,
        proposal.PayloadJson, proposal.RationaleMarkdown, proposal.Status,
        proposal.AuthorKind.ToString(), proposal.AuthorUserId, proposal.AuthorTokenId,
        proposal.AuthorProfileKey, proposal.AuthorRole, proposal.CreatedAt, proposal.ExpiresAt,
        proposal.DecidedAt, proposal.DecidedByUserId, proposal.DecisionNote, proposal.AcceptedPayloadJson,
        proposal.AppliedRevision, proposal.ConflictRevision, proposal.ConflictReason,
        proposal.SupersededById, proposal.StateRevision, proposal.ApplyCommandId);
}

/// <summary>Strict target-specific proposal payload validation and content application.</summary>
internal static class ProposalContentValidation
{
    public static bool TryValidatePayload(ContentTargetKind kind, string? payload, out string? error)
    {
        error = null;
        try
        {
            Proposal.ValidatePayload(payload);
            using var document = JsonDocument.Parse(payload!);
            var root = document.RootElement;
            switch (kind)
            {
                case ContentTargetKind.ContextSection:
                    Exact(root, "title", "bodyMarkdown");
                    Required(root, "title", 500);
                    Required(root, "bodyMarkdown", 30_000);
                    break;
                case ContentTargetKind.Decision:
                    Exact(root, "title", "context", "decisionBody", "consequences");
                    Required(root, "title", 200);
                    Required(root, "context", 5_000);
                    Required(root, "decisionBody", 20_000);
                    Optional(root, "consequences", 5_000);
                    break;
                case ContentTargetKind.Constraint:
                    Exact(root, "body", "scopePath");
                    Required(root, "body", 5_000);
                    Optional(root, "scopePath", 1_000);
                    break;
                case ContentTargetKind.Milestone:
                    Exact(root, "name", "description", "order");
                    Required(root, "name", 200);
                    Optional(root, "description", 5_000);
                    if (!root.GetProperty("order").TryGetInt32(out _))
                        throw new ArgumentException("Milestone order must be an integer.");
                    break;
                case ContentTargetKind.ProjectDescription:
                    Exact(root, "description");
                    Optional(root, "description", 20_000);
                    break;
                case ContentTargetKind.TaskDescription:
                    Exact(root, "description", "reviewNotes");
                    Optional(root, "description", 20_000);
                    Optional(root, "reviewNotes", 20_000);
                    break;
                default:
                    throw new ArgumentException("This target kind does not support content proposals.");
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    public static async Task<ProposalTarget?> LoadTargetAsync(
        IBeaconDb db, Guid projectId, ContentTargetKind kind, Guid targetId, CancellationToken ct)
    {
        switch (kind)
        {
            case ContentTargetKind.ContextSection:
                {
                    var target = await db.ContextSections.FirstOrDefaultAsync(
                        item => item.Id == targetId && item.ProjectId == projectId, ct);
                    return target is null ? null : new ProposalTarget(target, target.Revision,
                        target.ReviewState == ContextReviewState.Reviewed);
                }
            case ContentTargetKind.Decision:
                {
                    var target = await db.Decisions.FirstOrDefaultAsync(
                        item => item.Id == targetId && item.ProjectId == projectId, ct);
                    return target is null ? null : new ProposalTarget(target, target.Revision,
                        target.Status == DecisionStatus.Accepted);
                }
            case ContentTargetKind.Constraint:
                {
                    var target = await db.Constraints.FirstOrDefaultAsync(
                        item => item.Id == targetId && item.ProjectId == projectId, ct);
                    return target is null ? null : new ProposalTarget(target, target.Revision,
                        target.Status == ConstraintStatus.Active);
                }
            case ContentTargetKind.Milestone:
                {
                    var target = await db.Milestones.FirstOrDefaultAsync(
                        item => item.Id == targetId && item.ProjectId == projectId, ct);
                    return target is null ? null : new ProposalTarget(target, target.Revision, true);
                }
            case ContentTargetKind.ProjectDescription:
                {
                    if (targetId != projectId)
                        return null;
                    var target = await db.Projects.FirstOrDefaultAsync(item => item.Id == projectId, ct);
                    return target is null ? null : new ProposalTarget(target, target.Revision, true);
                }
            case ContentTargetKind.TaskDescription:
                {
                    var target = await db.Tasks.FirstOrDefaultAsync(
                        item => item.Id == targetId && item.ProjectId == projectId, ct);
                    return target is null ? null : new ProposalTarget(target, target.ContentRevision, true);
                }
            default:
                return null;
        }
    }

    public static void Apply(object target, ContentTargetKind kind, string payload, string changedByType, string changedById)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        switch (kind)
        {
            case ContentTargetKind.ContextSection:
                ((ContextSection)target).Update(Required(root, "title", 500), Required(root, "bodyMarkdown", 30_000),
                    changedByType, changedById);
                break;
            case ContentTargetKind.Decision:
                ((Decision)target).UpdateContent(Required(root, "title", 200), Required(root, "context", 5_000),
                    Required(root, "decisionBody", 20_000), Optional(root, "consequences", 5_000));
                break;
            case ContentTargetKind.Constraint:
                ((Constraint)target).UpdateContent(Required(root, "body", 5_000), Optional(root, "scopePath", 1_000));
                break;
            case ContentTargetKind.Milestone:
                ((Milestone)target).Update(Required(root, "name", 200), Optional(root, "description", 5_000),
                    root.GetProperty("order").GetInt32());
                break;
            case ContentTargetKind.ProjectDescription:
                ((Project)target).RestoreDescriptionFromHistory(Optional(root, "description", 20_000));
                break;
            case ContentTargetKind.TaskDescription:
                ((TaskItem)target).RestoreContentFromHistory(Optional(root, "description", 20_000),
                    Optional(root, "reviewNotes", 20_000));
                break;
            default:
                throw new ArgumentException("This target kind does not support content proposals.");
        }
    }

    public static int GetRevision(object target) => target switch
    {
        ContextSection section => section.Revision,
        Decision decision => decision.Revision,
        Constraint constraint => constraint.Revision,
        Milestone milestone => milestone.Revision,
        Project project => project.Revision,
        TaskItem task => task.ContentRevision,
        _ => throw new ArgumentException("Unknown revisioned target.", nameof(target))
    };

    private static void Exact(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Proposal payload must be a JSON object.");
        var actual = root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (actual.Count != names.Length || names.Any(name => !actual.Contains(name)))
            throw new ArgumentException("Proposal payload contains missing or unsupported fields.");
    }

    private static string Required(JsonElement root, string name, int maximum)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"'{name}' must be a string.");
        var value = property.GetString()!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw new ArgumentException($"'{name}' must contain 1-{maximum} characters.");
        return value;
    }

    private static string? Optional(JsonElement root, string name, int maximum)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"'{name}' must be a string or null.");
        var value = property.GetString();
        if (value?.Length > maximum)
            throw new ArgumentException($"'{name}' must not exceed {maximum} characters.");
        return value;
    }

    public sealed record ProposalTarget(object Entity, int Revision, bool Reviewable);
}
