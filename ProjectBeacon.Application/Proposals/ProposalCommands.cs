namespace ProjectBeacon.Application.Proposals;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
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
    int StateRevision);

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
    private readonly TimeProvider _timeProvider;

    public CreateProposalHandler(
        IBeaconDbFactory dbFactory,
        IAuditWriter auditWriter,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<ProposalDto>> HandleAsync(CreateProposalCommand command, CancellationToken ct = default)
    {
        if (command.ProjectId == Guid.Empty || command.Request.TargetId == Guid.Empty)
            return Result.Failure<ProposalDto>("ProjectId and TargetId are required.");
        if (command.Request.Operation != ProposalOperation.Update)
            return Result.Failure<ProposalDto>("Only Update proposals are currently supported.");
        if (!Enum.IsDefined(command.Request.TargetKind))
            return Result.Failure<ProposalDto>("Unknown proposal target kind.");
        if (!ProposalContentValidation.TryValidatePayload(command.Request.TargetKind, command.Request.PayloadJson, out var validationError))
            return Result.Failure<ProposalDto>(validationError!);
        if (SecretPattern.IsMatch(command.Request.PayloadJson)
            || SecretPattern.IsMatch(command.Request.RationaleMarkdown ?? string.Empty))
            return Result.Failure<ProposalDto>("Proposal content appears to contain a credential or private key. Remove secrets before submitting.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateDbContext();

        if (!command.Actor.IsHuman && !command.Actor.IsApiToken)
            return Result.Forbidden<ProposalDto>();

        if (command.Actor.IsApiToken && command.Actor.ProjectId != command.ProjectId)
            return Result.Forbidden<ProposalDto>("The API token is not bound to this project.");

        var author = await ResolveAuthorAsync(db, command, ct);
        if (!author.Success)
            return Result.Failure<ProposalDto>(author.Error ?? "The proposal author is not authorized.");

        var existingByKey = await db.Proposals.FirstOrDefaultAsync(
            proposal => proposal.ProjectId == command.ProjectId
                && proposal.IdempotencyKey == command.Request.IdempotencyKey,
            ct);
        if (existingByKey is not null)
        {
            if (SameRequest(existingByKey, command.Request, author.TaskId, author.SubtaskId, author.SessionId))
                return Result.Ok(Map(existingByKey));
            return Result.Failure<ProposalDto>("IdempotencyKey has already been used for a different proposal.", ErrorKind.Conflict);
        }

        var target = await ProposalContentValidation.LoadTargetAsync(db, command.ProjectId, command.Request.TargetKind, command.Request.TargetId, ct);
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

        var baseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseVersion.ContentJson))).ToLowerInvariant();
        var projectOrgId = await db.Projects
            .Where(project => project.Id == command.ProjectId)
            .Select(project => (Guid?)project.OrgId)
            .FirstOrDefaultAsync(ct);
        if (projectOrgId is null)
            return Result.Failure<ProposalDto>("Project not found.");

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
                command.Request.RationaleMarkdown,
                command.Actor.IsHuman ? ProposalAuthorKind.Human : ProposalAuthorKind.Agent,
                command.Actor.IsHuman ? command.Actor.UserId : null,
                command.Actor.IsApiToken ? command.Actor.TokenId : null,
                author.ProfileKey,
                author.Role,
                command.Request.IdempotencyKey,
                now);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<ProposalDto>(exception.Message);
        }

        if (target.Revision != command.Request.BaseRevision)
            proposal.MarkConflicted(target.Revision, "BaseRevisionChanged", now);

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                if (proposal.Status == ProposalStatus.Pending && author.SubtaskId is { } sourceSubtask)
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
                        StageAudit(db, command.Actor, item, projectOrgId.Value, "proposal.superseded", ProposalStatus.Superseded);
                    }
                    if (previous.Count > 0)
                        await db.SaveChangesAsync(ct);
                }

                db.Proposals.Add(proposal);
                StageAudit(db, command.Actor, proposal, projectOrgId.Value, "proposal.created", proposal.Status);
                if (proposal.Status == ProposalStatus.Conflicted)
                    StageAudit(db, command.Actor, proposal, projectOrgId.Value, "proposal.conflicted", ProposalStatus.Conflicted);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Result.Ok(Map(proposal));
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ProposalDto>("A pending proposal changed while this proposal was being submitted. Refresh and retry.", ErrorKind.Conflict);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<ProposalDto>("The proposal could not be saved because another request changed the same proposal scope. Refresh and retry.", ErrorKind.Conflict);
        }
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
            || !string.Equals(subtask.ProfileKey, "knowledge-curator", StringComparison.Ordinal)
            || !subtask.AllowedMcpTools.Contains("knowledge_propose", StringComparer.Ordinal))
            return ProposalAuthorScope.Forbidden("The active subtask profile is not granted knowledge proposal access.");

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
        Guid? taskId,
        Guid? subtaskId,
        Guid? sessionId) =>
        existing.TargetKind == request.TargetKind
        && existing.TargetId == request.TargetId
        && existing.Operation == request.Operation
        && existing.BaseRevision == request.BaseRevision
        && existing.PayloadJson == request.PayloadJson
        && existing.RationaleMarkdown == request.RationaleMarkdown
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

    public AcceptProposalHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter auditWriter,
        ContentWriter contentWriter,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
        _contentWriter = contentWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<ProposalDto>> HandleAsync(AcceptProposalCommand command, CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<ProposalDto>("Only a human can accept a proposal.");

        await using var db = _dbFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var proposal = await db.Proposals.FirstOrDefaultAsync(
                    item => item.Id == command.ProposalId && item.ProjectId == command.ProjectId, ct);
                if (proposal is null)
                    return Result.Failure<ProposalDto>("Proposal not found.");

                if (proposal.Status == ProposalStatus.Accepted)
                {
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

                if (proposal.TargetKind is ContentTargetKind.Decision or ContentTargetKind.Constraint)
                {
                    var governance = await _authorization.CanAsync(
                        db, command.Actor, ResourceType.Project, AuthAction.Administer,
                        projectId: proposal.ProjectId, ct: ct);
                    if (!governance.Success)
                        return Result.Forbidden<ProposalDto>("Only a project or organization Owner/Admin can accept Decision or Constraint proposals.");
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

                try
                {
                    ProposalContentValidation.Apply(target.Entity, proposal.TargetKind, payload, command.Actor.UserId.Value);
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
                    _contentWriter.AddVersion(db, target.Entity, "Human", command.Actor.UserId.Value.ToString("D"),
                        proposalId: proposal.Id);

                proposal.Accept(command.Actor.UserId.Value, appliedRevision,
                    command.AcceptedPayloadJson, now);
                StageAudit(db, command.Actor, proposal, orgId.Value, "proposal.accepted", ProposalStatus.Accepted);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Result.Ok(ProposalMapper.Map(proposal));
            });
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

    public RejectProposalHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter auditWriter,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        if (proposal.Status == ProposalStatus.Rejected)
            return Result.Ok(ProposalMapper.Map(proposal));
        if (proposal.Status != ProposalStatus.Pending)
            return Result.Failure<ProposalDto>($"Proposal cannot be rejected in status {proposal.Status}.");

        if (proposal.TargetKind is ContentTargetKind.Decision or ContentTargetKind.Constraint)
        {
            var governance = await _authorization.CanAsync(
                db, command.Actor, ResourceType.Project, AuthAction.Administer,
                projectId: proposal.ProjectId, ct: ct);
            if (!governance.Success)
                return Result.Forbidden<ProposalDto>("Only a project or organization Owner/Admin can reject Decision or Constraint proposals.");
        }

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
        await using var db = _dbFactory.CreateDbContext();
        var proposal = await db.Proposals.FirstOrDefaultAsync(
            item => item.Id == command.ProposalId && item.ProjectId == command.ProjectId, ct);
        if (proposal is null)
            return Result.Failure<ProposalDto>("Proposal not found.");
        if (proposal.Status == ProposalStatus.Withdrawn)
            return Result.Ok(ProposalMapper.Map(proposal));
        if (proposal.Status != ProposalStatus.Pending)
            return Result.Failure<ProposalDto>($"Proposal cannot be withdrawn in status {proposal.Status}.");

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
                proposal.ConflictRevision, proposal.ConflictReason, proposal.SupersededById, proposal.StateRevision))
            .ToListAsync(ct);
        return Result.Ok((IReadOnlyList<ProposalDto>)items);
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
        proposal.SupersededById, proposal.StateRevision);
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

    public static void Apply(object target, ContentTargetKind kind, string payload, Guid userId)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        switch (kind)
        {
            case ContentTargetKind.ContextSection:
                ((ContextSection)target).Update(Required(root, "title", 500), Required(root, "bodyMarkdown", 30_000),
                    "user", userId.ToString("D"));
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
