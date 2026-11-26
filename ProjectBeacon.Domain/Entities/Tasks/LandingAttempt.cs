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
    public string? EscalationCode { get; private set; }
    public int GitPolicyRevision { get; private set; } = 1;
    public string GitPolicySnapshotJson { get; private set; } = "{}";
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
            || targetBranch.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or '-' or '/')))
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

    /// <summary>
    /// Pins the validated project Git policy to this attempt before the first command is queued.
    /// The snapshot is immutable for the lifetime of the attempt.
    /// </summary>
    public void SetGitPolicySnapshot(int revision, string snapshotJson)
    {
        EnsureStatus(LandingAttemptStatus.PreviewPending);
        if (!string.Equals(GitPolicySnapshotJson, "{}", StringComparison.Ordinal))
            throw new InvalidOperationException("The Git policy snapshot is already pinned for this landing attempt.");
        if (revision < 1 || string.IsNullOrWhiteSpace(snapshotJson) || snapshotJson.Length > 16_384)
            throw new ArgumentException("A valid Git policy revision and snapshot are required.");
        using var document = System.Text.Json.JsonDocument.Parse(snapshotJson);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ArgumentException("Git policy snapshot must be a JSON object.", nameof(snapshotJson));
        GitPolicyRevision = revision;
        GitPolicySnapshotJson = snapshotJson;
        Touch();
    }

    /// <summary>Sets a short machine-readable escalation reason without changing the Git outcome.</summary>
    public void SetEscalationCode(string? code)
    {
        if (code is not null
            && (code.Length > 80 || code.Length == 0
                || code.Any(character => !(char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '_' or ':' or '-'))))
            throw new ArgumentException("Escalation code must be a short machine-readable code.", nameof(code));
        EscalationCode = code;
        Touch();
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
        if (!string.Equals(TargetBranch, targetBranch, StringComparison.Ordinal)
            && !string.Equals(targetBranch, "beacon/integration", StringComparison.Ordinal))
            throw new InvalidOperationException("Preview target branch must be the reviewed base or the ProjectBeacon integration branch.");
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

    /// <summary>
    /// Marks the attempt as being dispatched to its workstation. The timestamp is a
    /// short recovery lease: a repeated API request must not enqueue a duplicate command
    /// while the first request is still creating it, but can recover after a process crash.
    /// </summary>
    public void MarkDispatchPending(string action)
    {
        var marker = action switch
        {
            "Preview" => "PreviewEnqueuePending",
            "Execute" => "ExecuteEnqueuePending",
            "Revert" => "RevertEnqueuePending",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        switch (action)
        {
            case "Preview":
                EnsureStatus(LandingAttemptStatus.PreviewPending);
                if (string.Equals(GitPolicySnapshotJson, "{}", StringComparison.Ordinal))
                    throw new InvalidOperationException("A Git policy snapshot must be pinned before Preview dispatch.");
                break;
            case "Execute":
                if (Status == LandingAttemptStatus.Previewed)
                    Status = LandingAttemptStatus.ExecutePending;
                else
                    EnsureStatus(LandingAttemptStatus.ExecutePending);
                break;
            case "Revert":
                if (Status is LandingAttemptStatus.Landed
                    or LandingAttemptStatus.LandedUnverified
                    or LandingAttemptStatus.RevertFailed)
                    Status = LandingAttemptStatus.RevertPending;
                else
                    EnsureStatus(LandingAttemptStatus.RevertPending);
                break;
        }

        FailureCode = marker;
        EscalationCode = null;
        Touch();
    }

    /// <summary>Clears a dispatch lease once the durable workstation command has been created.</summary>
    public void ClearDispatchPending(string action)
    {
        var marker = action switch
        {
            "Preview" => "PreviewEnqueuePending",
            "Execute" => "ExecuteEnqueuePending",
            "Revert" => "RevertEnqueuePending",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (!string.Equals(FailureCode, marker, StringComparison.Ordinal))
            throw new InvalidOperationException($"The landing attempt is not in the {action} dispatch phase.");
        FailureCode = null;
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
        FailureCode = null;
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

    /// <summary>
    /// Preserves an in-flight execute/revert when the workstation acknowledgement is lost.
    /// The target ref may already have moved; a retry must reconcile the attempt marker before
    /// deciding whether to mutate the target again.
    /// </summary>
    public void MarkCommandCompletionUnknown()
    {
        if (Status is not (LandingAttemptStatus.ExecutePending or LandingAttemptStatus.RevertPending))
            throw new InvalidOperationException($"Cannot mark a landing command uncertain in status {Status}.");
        FailureCode = "CommandCompletionUnknown";
        Touch();
    }

    /// <summary>
    /// Records that GitHub accepted this exact PR merge request but the PR has not
    /// reached MERGED yet (for example, it is waiting in a merge queue). The attempt
    /// remains ExecutePending until the pinned workstation verifies the final merge.
    /// </summary>
    public void MarkGitHubMergeQueued(string? mergeRequestId)
    {
        EnsureStatus(LandingAttemptStatus.ExecutePending);
        if (string.IsNullOrWhiteSpace(mergeRequestId))
        {
            // GitHub can report that a PR is already enqueued without returning a new
            // async-request UUID. The PR state remains the durable polling authority.
            FailureCode = "GitHubMergeQueued";
        }
        else
        {
            if (!Guid.TryParse(mergeRequestId, out var parsed) || parsed == Guid.Empty)
                throw new ArgumentException("GitHub asynchronous merge request ID must be a GUID.", nameof(mergeRequestId));
            FailureCode = "GitHubMergeQueued:" + parsed.ToString("D");
        }
        Touch();
    }

    public bool TryReadGitHubMergeRequestId(out string? mergeRequestId)
    {
        mergeRequestId = null;
        const string prefix = "GitHubMergeQueued:";
        if (Status != LandingAttemptStatus.ExecutePending || FailureCode is null)
            return false;
        if (string.Equals(FailureCode, "GitHubMergeQueued", StringComparison.Ordinal))
            return true;
        if (!FailureCode.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(FailureCode[prefix.Length..], "D", out var parsed)
            || parsed == Guid.Empty)
            return false;

        mergeRequestId = parsed.ToString("D");
        return true;
    }

    public void ClearCommandCompletionUnknown()
    {
        if (Status is not (LandingAttemptStatus.ExecutePending or LandingAttemptStatus.RevertPending)
            || !string.Equals(FailureCode, "CommandCompletionUnknown", StringComparison.Ordinal))
            throw new InvalidOperationException("The landing attempt is not awaiting command reconciliation.");
        FailureCode = null;
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
        FailureCode = null;
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
