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
    public Guid DeviceId { get; private set; }
    public LandingAttemptStatus Status { get; private set; }
    public string Rationale { get; private set; } = string.Empty;
    public string SourceBranch { get; private set; } = string.Empty;
    public string SourceSha { get; private set; } = string.Empty;
    public string ReviewTreeSha { get; private set; } = string.Empty;
    public string ReviewCommandHash { get; private set; } = string.Empty;
    public string TargetBranch { get; private set; } = string.Empty;
    public string? TargetHeadSha { get; private set; }
    public string? CandidateTreeSha { get; private set; }
    public string? CandidateCheckOutputHash { get; private set; }
    public int? CandidateCheckExitCode { get; private set; }
    public string? ConflictFilesJson { get; private set; }
    public string? FailureCode { get; private set; }
    public string? MergeCommitSha { get; private set; }
    public string? RevertCommitSha { get; private set; }
    public int? PostMergeCheckExitCode { get; private set; }
    public string? PostMergeCheckOutputHash { get; private set; }
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
        Guid deviceId,
        string targetBranch,
        string sourceSha,
        string reviewTreeSha,
        string reviewCommandHash,
        string rationale)
    {
        if (projectId == Guid.Empty || taskId == Guid.Empty || reviewRunId == Guid.Empty
            || requestedByUserId == Guid.Empty || deviceId == Guid.Empty)
            throw new ArgumentException("Project, task, review run, requesting user, and workstation are required.");
        if (string.IsNullOrWhiteSpace(targetBranch) || targetBranch.Length > 200
            || targetBranch.Contains("..", StringComparison.Ordinal)
            || targetBranch.Contains("@{", StringComparison.Ordinal)
            || targetBranch.Contains('\\')
            || targetBranch.Any(char.IsControl))
            throw new ArgumentException("Target branch is invalid.", nameof(targetBranch));
        if (!IsGitSha(sourceSha))
            throw new ArgumentException("Source SHA is invalid.", nameof(sourceSha));
        if (!IsGitSha(reviewTreeSha))
            throw new ArgumentException("Reviewed tree SHA is invalid.", nameof(reviewTreeSha));
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
        attempt.DeviceId = deviceId;
        attempt.Status = LandingAttemptStatus.PreviewPending;
        attempt.Rationale = normalizedRationale;
        attempt.SourceBranch = $"beacon/task/{taskId:N}";
        attempt.TargetBranch = targetBranch.Trim();
        attempt.SourceSha = sourceSha.ToLowerInvariant();
        attempt.ReviewTreeSha = reviewTreeSha.ToLowerInvariant();
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
        if (!string.Equals(TargetBranch, targetBranch, StringComparison.Ordinal))
            throw new InvalidOperationException("Preview target branch does not match the branch pinned by the review proof.");
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

    public void RecordLanded(
        string mergeCommitSha,
        string candidateTreeSha,
        int postMergeCheckExitCode,
        string postMergeCheckOutputHash)
    {
        EnsureStatus(LandingAttemptStatus.ExecutePending);
        if (!IsGitSha(mergeCommitSha) || !IsGitSha(candidateTreeSha)
            || !string.Equals(CandidateTreeSha, candidateTreeSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The executed merge does not match the previewed candidate tree.");
        if (postMergeCheckExitCode != 0 || !IsSha256(postMergeCheckOutputHash))
            throw new InvalidOperationException("A successful post-merge check with a SHA-256 output hash is required.");
        MergeCommitSha = mergeCommitSha.ToLowerInvariant();
        PostMergeCheckExitCode = postMergeCheckExitCode;
        PostMergeCheckOutputHash = postMergeCheckOutputHash.ToLowerInvariant();
        Status = LandingAttemptStatus.Landed;
        ExecutedAt = DateTime.UtcNow;
        Touch();
    }

    /// <summary>Records a merge that reached the target branch but failed post-merge verification.</summary>
    public void RecordLandedUnverified(
        string mergeCommitSha,
        string failureCode,
        int? postMergeCheckExitCode = null,
        string? postMergeCheckOutputHash = null)
    {
        EnsureStatus(LandingAttemptStatus.ExecutePending);
        if (!IsGitSha(mergeCommitSha))
            throw new ArgumentException("Merge commit SHA is invalid.", nameof(mergeCommitSha));
        if (postMergeCheckOutputHash is not null && !IsSha256(postMergeCheckOutputHash))
            throw new ArgumentException("Post-merge check output hash is invalid.", nameof(postMergeCheckOutputHash));
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (failureCode.Length > 80 || failureCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')))
            throw new ArgumentException("Failure code must be a short machine-readable code.", nameof(failureCode));
        MergeCommitSha = mergeCommitSha.ToLowerInvariant();
        PostMergeCheckExitCode = postMergeCheckExitCode;
        PostMergeCheckOutputHash = postMergeCheckOutputHash?.ToLowerInvariant();
        FailureCode = failureCode;
        Status = LandingAttemptStatus.LandedUnverified;
        ExecutedAt = DateTime.UtcNow;
        Touch();
    }

    public void MarkRevertPending()
    {
        if (Status is not (LandingAttemptStatus.Landed or LandingAttemptStatus.LandedUnverified))
            throw new InvalidOperationException($"Cannot revert a landing attempt in status {Status}.");
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

    /// <summary>Records a ref update that succeeded while the checked-out target worktree could not be safely refreshed.</summary>
    public void RecordRevertedUnverified(string revertCommitSha, string failureCode)
    {
        EnsureStatus(LandingAttemptStatus.RevertPending);
        if (!IsGitSha(revertCommitSha))
            throw new ArgumentException("Revert commit SHA is invalid.", nameof(revertCommitSha));
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (failureCode.Length > 80 || failureCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')))
            throw new ArgumentException("Failure code must be a short machine-readable code.", nameof(failureCode));
        RevertCommitSha = revertCommitSha.ToLowerInvariant();
        FailureCode = failureCode;
        Status = LandingAttemptStatus.RevertedUnverified;
        RevertedAt = DateTime.UtcNow;
        Touch();
    }

    public void Fail(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (failureCode.Length > 80 || failureCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')))
            throw new ArgumentException("Failure code must be a short machine-readable code.", nameof(failureCode));
        if (Status is LandingAttemptStatus.Landed or LandingAttemptStatus.LandedUnverified
            or LandingAttemptStatus.Reverted or LandingAttemptStatus.RevertedUnverified
            or LandingAttemptStatus.Conflicted)
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
