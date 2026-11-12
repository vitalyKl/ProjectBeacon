namespace ProjectBeacon.Domain.Entities.Content;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// A revision-bound proposal to create a Milestone or change existing project content.
/// Create proposals reserve their target id before review; payloads are untrusted input and
/// only a human can transition a Pending proposal to an accepted or rejected state.
/// </summary>
public sealed class Proposal : Entity, IProjectScoped
{
    public const int MaxPayloadLength = 40_000;
    public const int MaxRationaleLength = 1_000;
    public const int MaxIdempotencyKeyLength = 128;
    public const int MaxAuthorModelLength = 200;
    public static readonly TimeSpan TimeToLive = TimeSpan.FromDays(30);

    public Proposal() { }

    public Guid ProjectId { get; private set; }
    public Guid? TaskId { get; private set; }
    public Guid? SubtaskId { get; private set; }
    public Guid? SessionId { get; private set; }
    public ContentTargetKind TargetKind { get; private set; }
    public Guid TargetId { get; private set; }
    public ProposalOperation Operation { get; private set; }
    public int BaseRevision { get; private set; }
    public string BaseHash { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = "{}";
    public string RationaleMarkdown { get; private set; } = string.Empty;
    public ProposalStatus Status { get; private set; }
    public ProposalAuthorKind AuthorKind { get; private set; }
    public Guid? AuthorUserId { get; private set; }
    public Guid? AuthorTokenId { get; private set; }
    public string? AuthorProfileKey { get; private set; }
    public string? AuthorRole { get; private set; }
    public string? AuthorModel { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public Guid? DecidedByUserId { get; private set; }
    public string? DecisionNote { get; private set; }
    public string? AcceptedPayloadJson { get; private set; }
    public int? AppliedRevision { get; private set; }
    public int? ConflictRevision { get; private set; }
    public string? ConflictReason { get; private set; }
    public Guid? SupersededById { get; private set; }
    public Guid? ApplyCommandId { get; private set; }

    /// <summary>Optimistic-concurrency token for proposal lifecycle transitions.</summary>
    public int StateRevision { get; private set; } = 1;

    public static Proposal Create(
        Guid projectId,
        Guid? taskId,
        Guid? subtaskId,
        Guid? sessionId,
        ContentTargetKind targetKind,
        Guid targetId,
        ProposalOperation operation,
        int baseRevision,
        string baseHash,
        string payloadJson,
        string rationaleMarkdown,
        ProposalAuthorKind authorKind,
        Guid? authorUserId,
        Guid? authorTokenId,
        string? authorProfileKey,
        string? authorRole,
        string idempotencyKey,
        DateTime createdAtUtc,
        string? authorModel = null)
    {
        if (projectId == Guid.Empty || targetId == Guid.Empty)
            throw new ArgumentException("ProjectId and TargetId are required.");
        if (!Enum.IsDefined(targetKind) || !Enum.IsDefined(operation) || !Enum.IsDefined(authorKind))
            throw new ArgumentOutOfRangeException(nameof(targetKind));

        if (operation == ProposalOperation.Create)
        {
            if (targetKind != ContentTargetKind.Milestone || baseRevision != 0)
                throw new ArgumentException(
                    "Create proposals are currently supported for Milestone with BaseRevision 0.",
                    nameof(operation));
        }
        else if (baseRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(baseRevision));
        }

        if (operation == ProposalOperation.Delete
            && targetKind is not (ContentTargetKind.ContextSection
                or ContentTargetKind.Decision
                or ContentTargetKind.Constraint
                or ContentTargetKind.Milestone))
            throw new ArgumentException(
                "Delete proposals are supported only for ContextSection, Decision, Constraint and Milestone.",
                nameof(operation));
        if (targetKind == ContentTargetKind.RepoPatch && operation != ProposalOperation.Update)
            throw new ArgumentException("RepoPatch supports Update only.", nameof(operation));
        ArgumentException.ThrowIfNullOrWhiteSpace(baseHash);
        if (baseHash.Length != 64 || !baseHash.All(Uri.IsHexDigit))
            throw new ArgumentException("BaseHash must be a SHA-256 hexadecimal digest.", nameof(baseHash));
        ValidatePayload(payloadJson);
        ValidateRationale(rationaleMarkdown);
        ValidateIdempotencyKey(idempotencyKey);
        if (createdAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("CreatedAt must be UTC.", nameof(createdAtUtc));
        if (authorKind == ProposalAuthorKind.Human && (!authorUserId.HasValue || authorTokenId.HasValue))
            throw new ArgumentException("A human proposal must identify exactly one user.");
        if (authorKind == ProposalAuthorKind.Agent && (!authorTokenId.HasValue || authorUserId.HasValue))
            throw new ArgumentException("An agent proposal must identify exactly one API token.");
        if (subtaskId.HasValue != sessionId.HasValue)
            throw new ArgumentException("SubtaskId and SessionId must either both be present or both be absent.");
        if (authorProfileKey?.Length > 100 || authorRole?.Length > 32
            || authorModel?.Length > MaxAuthorModelLength)
            throw new ArgumentException("Author profile, role or model exceeds its maximum length.");

        var proposal = Entity.New<Proposal>();
        proposal.ProjectId = projectId;
        proposal.TaskId = taskId;
        proposal.SubtaskId = subtaskId;
        proposal.SessionId = sessionId;
        proposal.TargetKind = targetKind;
        proposal.TargetId = targetId;
        proposal.Operation = operation;
        proposal.BaseRevision = baseRevision;
        proposal.BaseHash = baseHash.ToLowerInvariant();
        proposal.PayloadJson = payloadJson;
        proposal.RationaleMarkdown = rationaleMarkdown.Trim();
        proposal.Status = ProposalStatus.Pending;
        proposal.AuthorKind = authorKind;
        proposal.AuthorUserId = authorUserId;
        proposal.AuthorTokenId = authorTokenId;
        proposal.AuthorProfileKey = authorProfileKey;
        proposal.AuthorRole = authorRole;
        proposal.AuthorModel = string.IsNullOrWhiteSpace(authorModel) ? null : authorModel.Trim();
        proposal.IdempotencyKey = idempotencyKey.Trim();
        proposal.CreatedAt = createdAtUtc;
        proposal.ExpiresAt = createdAtUtc.Add(TimeToLive);
        return proposal;
    }

    public void Accept(Guid humanUserId, int appliedRevision, string? acceptedPayloadJson, DateTime decidedAtUtc)
    {
        EnsurePending();
        if (humanUserId == Guid.Empty)
            throw new ArgumentException("A human decision-maker is required.", nameof(humanUserId));
        if (appliedRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(appliedRevision));
        if (decidedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Decision time must be UTC.", nameof(decidedAtUtc));
        if (acceptedPayloadJson is not null)
            ValidatePayload(acceptedPayloadJson);

        Status = ProposalStatus.Accepted;
        DecidedAt = decidedAtUtc;
        DecidedByUserId = humanUserId;
        AcceptedPayloadJson = acceptedPayloadJson;
        AppliedRevision = appliedRevision;
        DecisionNote = null;
        StateRevision++;
    }

    /// <summary>Accepts a proposal under an explicitly configured project Auto policy.</summary>
    public void AutoAccept(int appliedRevision, string? acceptedPayloadJson, DateTime decidedAtUtc)
    {
        EnsurePending();
        if (appliedRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(appliedRevision));
        if (decidedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Decision time must be UTC.", nameof(decidedAtUtc));
        if (acceptedPayloadJson is not null)
            ValidatePayload(acceptedPayloadJson);

        Status = ProposalStatus.Accepted;
        DecidedAt = decidedAtUtc;
        DecidedByUserId = null;
        AcceptedPayloadJson = acceptedPayloadJson;
        AppliedRevision = appliedRevision;
        DecisionNote = null;
        StateRevision++;
    }

    /// <summary>Records that an approved RepoPatch has been queued for the workstation.</summary>
    public void StartApply(Guid commandId, Guid? reviewerUserId, DateTime queuedAtUtc)
    {
        EnsurePending();
        if (commandId == Guid.Empty)
            throw new ArgumentException("An apply command id is required.", nameof(commandId));
        if (reviewerUserId == Guid.Empty)
            throw new ArgumentException("Reviewer id cannot be empty.", nameof(reviewerUserId));
        if (queuedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Apply time must be UTC.", nameof(queuedAtUtc));

        Status = ProposalStatus.Applying;
        ApplyCommandId = commandId;
        DecidedAt = queuedAtUtc;
        DecidedByUserId = reviewerUserId;
        DecisionNote = null;
        StateRevision++;
    }

    /// <summary>Marks the RepoPatch accepted only after the workstation confirms it was applied.</summary>
    public void CompleteApply(DateTime appliedAtUtc)
    {
        EnsureApplying();
        if (appliedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Apply time must be UTC.", nameof(appliedAtUtc));
        Status = ProposalStatus.Accepted;
        DecidedAt = appliedAtUtc;
        StateRevision++;
    }

    /// <summary>Records a client-side path, base-hash or patch conflict without changing repository files.</summary>
    public void ConflictApply(string reason, DateTime conflictAtUtc)
    {
        EnsureApplying();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 200)
            throw new ArgumentException("A short apply conflict reason is required.", nameof(reason));
        if (conflictAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Conflict time must be UTC.", nameof(conflictAtUtc));
        Status = ProposalStatus.Conflicted;
        ConflictReason = reason;
        DecidedAt = conflictAtUtc;
        StateRevision++;
    }

    public void Reject(Guid humanUserId, string? note, DateTime decidedAtUtc)
    {
        EnsurePending();
        if (humanUserId == Guid.Empty)
            throw new ArgumentException("A human decision-maker is required.", nameof(humanUserId));
        ValidateDecisionNote(note);
        if (decidedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Decision time must be UTC.", nameof(decidedAtUtc));

        Status = ProposalStatus.Rejected;
        DecidedAt = decidedAtUtc;
        DecidedByUserId = humanUserId;
        DecisionNote = NormalizeNote(note);
        StateRevision++;
    }

    public void Withdraw(DateTime decidedAtUtc)
    {
        EnsurePending();
        if (decidedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Decision time must be UTC.", nameof(decidedAtUtc));
        Status = ProposalStatus.Withdrawn;
        DecidedAt = decidedAtUtc;
        StateRevision++;
    }

    public void Supersede(Guid replacementProposalId, DateTime decidedAtUtc)
    {
        EnsurePending();
        if (replacementProposalId == Guid.Empty || replacementProposalId == Id)
            throw new ArgumentException("A different replacement proposal is required.", nameof(replacementProposalId));
        if (decidedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Decision time must be UTC.", nameof(decidedAtUtc));
        Status = ProposalStatus.Superseded;
        SupersededById = replacementProposalId;
        DecidedAt = decidedAtUtc;
        StateRevision++;
    }

    public bool Expire(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Current time must be UTC.", nameof(utcNow));
        if (Status != ProposalStatus.Pending || ExpiresAt > utcNow)
            return false;
        Status = ProposalStatus.Expired;
        DecidedAt = utcNow;
        StateRevision++;
        return true;
    }

    public void MarkConflicted(int? actualRevision, string reason, DateTime decidedAtUtc)
    {
        EnsurePending();
        if (actualRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(actualRevision));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 200)
            throw new ArgumentException("A short conflict reason is required.", nameof(reason));
        if (decidedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Decision time must be UTC.", nameof(decidedAtUtc));
        Status = ProposalStatus.Conflicted;
        ConflictRevision = actualRevision;
        ConflictReason = reason;
        DecidedAt = decidedAtUtc;
        StateRevision++;
    }

    public static void ValidatePayload(string? payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (payloadJson.Length > MaxPayloadLength)
            throw new ArgumentException($"Proposal payload must not exceed {MaxPayloadLength} characters.", nameof(payloadJson));
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new ArgumentException("Proposal payload must be a JSON object.", nameof(payloadJson));
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new ArgumentException("Proposal payload must be valid JSON.", nameof(payloadJson), exception);
        }
    }

    private static void ValidateRationale(string? rationaleMarkdown)
    {
        if (string.IsNullOrWhiteSpace(rationaleMarkdown))
            throw new ArgumentException("Proposal rationale is required.", nameof(rationaleMarkdown));
        if (rationaleMarkdown.Length > MaxRationaleLength)
            throw new ArgumentException($"Proposal rationale must not exceed {MaxRationaleLength} characters.", nameof(rationaleMarkdown));
    }

    private static void ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > MaxIdempotencyKeyLength
            || idempotencyKey.Any(char.IsControl))
            throw new ArgumentException($"IdempotencyKey must contain 1-{MaxIdempotencyKeyLength} printable characters.", nameof(idempotencyKey));
    }

    private static void ValidateDecisionNote(string? note)
    {
        if (note?.Length > MaxRationaleLength)
            throw new ArgumentException($"Decision note must not exceed {MaxRationaleLength} characters.", nameof(note));
    }

    private static string? NormalizeNote(string? note)
        => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private void EnsureApplying()
    {
        if (Status != ProposalStatus.Applying)
            throw new InvalidOperationException($"Cannot complete a RepoPatch in status {Status}.");
    }

    private void EnsurePending()
    {
        if (Status != ProposalStatus.Pending)
            throw new InvalidOperationException($"Cannot change a proposal in status {Status}.");
    }
}
