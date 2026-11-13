namespace ProjectBeacon.Domain.Entities.Tasks;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// Immutable review evidence and daemon-verified Git facts for one task landing attempt.
/// The status changes only through the guarded transition methods below.
/// </summary>
public sealed class LandingAttempt : Entity, IProjectScoped
{
    public LandingAttempt() { }

    public Guid ProjectId { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid ReviewRunId { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public LandingAttemptStatus Status { get; private set; }
    public string Rationale { get; private set; } = string.Empty;
    public string SourceBranch { get; private set; } = string.Empty;
    public string SourceSha { get; private set; } = string.Empty;
    public string ReviewCommandHash { get; private set; } = string.Empty;
    public string? TargetBranch { get; private set; }
    public string? TargetHeadSha { get; private set; }
    public string? CandidateTreeSha { get; private set; }
    public string? CandidateCheckOutputHash { get; private set; }
    public int? CandidateCheckExitCode { get; private set; }
    public string? ConflictFilesJson { get; private set; }
    public string? FailureCode { get; private set; }
    public string? MergeCommitSha { get; private set; }
    public string? RevertCommitSha { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? PreviewedAt { get; private set; }
    public DateTime? ExecutedAt { get; private set; }
    public DateTime? RevertedAt { get; private set; }

    public static LandingAttempt Start(
        Guid projectId,
        Guid taskId,
        Guid reviewRunId,
        Guid requestedByUserId,
        string sourceSha,
        string reviewCommandHash,
        string rationale)
    {
        if (projectId == Guid.Empty || taskId == Guid.Empty || reviewRunId == Guid.Empty || requestedByUserId == Guid.Empty)
            throw new ArgumentException("Project, task, review run, and requesting user are required.");
        if (!IsGitSha(sourceSha))
            throw new ArgumentException("Source SHA is invalid.", nameof(sourceSha));
        if (!IsSha256(reviewCommandHash))
            throw new ArgumentException("Review command hash is invalid.", nameof(reviewCommandHash));
        var normalizedRationale = rationale?.Trim() ?? string.Empty;
        if (normalizedRationale.Length is < 1 or > 1000)
            throw new ArgumentException("Landing rationale must contain 1 to 1000 characters.", nameof(rationale));

        var now = DateTime.UtcNow;
        var attempt = Entity.New<LandingAttempt>();
        attempt.ProjectId = projectId;
        attempt.TaskId = taskId;
        attempt.ReviewRunId = reviewRunId;
        attempt.RequestedByUserId = requestedByUserId;
        attempt.Status = LandingAttemptStatus.PreviewPending;
        attempt.Rationale = normalizedRationale;
        attempt.SourceBranch = $"beacon/task/{taskId:N}";
        attempt.SourceSha = sourceSha.ToLowerInvariant();
        attempt.ReviewCommandHash = reviewCommandHash.ToLowerInvariant();
        attempt.CreatedAt = now;
        attempt.UpdatedAt = now;
        return attempt;
    }

    public void RecordPreview(
        string targetBranch,
        string targetHeadSha,
        string? candidateTreeSha,
        string? candidateCheckOutputHash,
        int? candidateCheckExitCode,
        string? conflictFilesJson)
    {
        EnsureStatus(LandingAttemptStatus.PreviewPending);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);
        if (!IsGitSha(targetHeadSha))
            throw new ArgumentException("Target HEAD SHA is invalid.", nameof(targetHeadSha));
        if (candidateTreeSha is not null && !IsGitSha(candidateTreeSha))
            throw new ArgumentException("Candidate tree SHA is invalid.", nameof(candidateTreeSha));
        if (candidateCheckOutputHash is not null && !IsSha256(candidateCheckOutputHash))
            throw new ArgumentException("Candidate check output hash is invalid.", nameof(candidateCheckOutputHash));

        TargetBranch = targetBranch;
        TargetHeadSha = targetHeadSha.ToLowerInvariant();
        CandidateTreeSha = candidateTreeSha?.ToLowerInvariant();
        CandidateCheckOutputHash = candidateCheckOutputHash?.ToLowerInvariant();
        CandidateCheckExitCode = candidateCheckExitCode;
        ConflictFilesJson = conflictFilesJson;
        Status = string.IsNullOrWhiteSpace(conflictFilesJson)
            && candidateTreeSha is not null
            && candidateCheckExitCode == 0
                ? LandingAttemptStatus.Previewed
                : string.IsNullOrWhiteSpace(conflictFilesJson)
                    ? LandingAttemptStatus.Failed
                    : LandingAttemptStatus.Conflicted;
        FailureCode = Status == LandingAttemptStatus.Failed
            ? candidateCheckExitCode is null ? "PreviewFailed" : "CandidateCheckFailed"
            : null;
        PreviewedAt = DateTime.UtcNow;
        Touch();
    }

    public void MarkExecutePending()
    {
        EnsureStatus(LandingAttemptStatus.Previewed);
        Status = LandingAttemptStatus.ExecutePending;
        FailureCode = null;
        Touch();
    }

    public void RecordLanded(string mergeCommitSha, string candidateTreeSha)
    {
        EnsureStatus(LandingAttemptStatus.ExecutePending);
        if (!IsGitSha(mergeCommitSha) || !IsGitSha(candidateTreeSha)
            || !string.Equals(CandidateTreeSha, candidateTreeSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The executed merge does not match the previewed candidate tree.");
        MergeCommitSha = mergeCommitSha.ToLowerInvariant();
        Status = LandingAttemptStatus.Landed;
        ExecutedAt = DateTime.UtcNow;
        Touch();
    }

    public void MarkRevertPending()
    {
        EnsureStatus(LandingAttemptStatus.Landed);
        Status = LandingAttemptStatus.RevertPending;
        Touch();
    }

    public void RecordReverted(string revertCommitSha)
    {
        EnsureStatus(LandingAttemptStatus.RevertPending);
        if (!IsGitSha(revertCommitSha))
            throw new ArgumentException("Revert commit SHA is invalid.", nameof(revertCommitSha));
        RevertCommitSha = revertCommitSha.ToLowerInvariant();
        Status = LandingAttemptStatus.Reverted;
        RevertedAt = DateTime.UtcNow;
        Touch();
    }

    public void Fail(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (failureCode.Length > 80 || failureCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')))
            throw new ArgumentException("Failure code must be a short machine-readable code.", nameof(failureCode));
        if (Status is LandingAttemptStatus.Landed or LandingAttemptStatus.Reverted or LandingAttemptStatus.Conflicted)
            throw new InvalidOperationException($"Cannot fail a landing attempt in status {Status}.");
        Status = Status == LandingAttemptStatus.RevertPending
            ? LandingAttemptStatus.RevertFailed
            : LandingAttemptStatus.Failed;
        FailureCode = failureCode;
        Touch();
    }

    private void EnsureStatus(LandingAttemptStatus expected)
    {
        if (Status != expected)
            throw new InvalidOperationException($"Expected landing status {expected}, found {Status}.");
    }

    private void Touch() => UpdatedAt = DateTime.UtcNow;

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
