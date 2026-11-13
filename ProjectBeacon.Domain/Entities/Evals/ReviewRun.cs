namespace ProjectBeacon.Domain.Entities.Evals;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// A project-scoped review of a task. Starts as <see cref="ReviewRunStatus.Started"/>.
/// <see cref="IsCheckProof"/> requires completion, a target run, an artifact ref, the approved command hash, and the checked Git source/tree SHAs.
/// </summary>
public sealed class ReviewRun : Entity, IProjectScoped
{
    public ReviewRun() { }

    public Guid ProjectId { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid? TargetRunId { get; private set; }
    public ReviewerType ReviewerType { get; private set; }
    public ReviewRunStatus Status { get; private set; }
    public string? Findings { get; private set; }
    public string? ArtifactRef { get; private set; }
    public string TranscriptRef { get; private set; } = string.Empty;
    public string? ReviewerActorId { get; private set; }
    public string? CheckCommandHash { get; private set; }
    public string? CheckedSourceSha { get; private set; }
    public string? CheckedTreeSha { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Starts a review in <see cref="ReviewRunStatus.Started"/>.
    /// </summary>
    public static ReviewRun Start(
        Guid projectId,
        Guid taskId,
        ReviewerType reviewerType,
        Guid? targetRunId = null,
        string? reviewerActorId = null)
    {
        var run = Entity.New<ReviewRun>();
        run.ProjectId = projectId;
        run.TaskId = taskId;
        run.ReviewerType = reviewerType;
        run.TargetRunId = targetRunId;
        run.ReviewerActorId = reviewerActorId;
        run.Status = ReviewRunStatus.Started;
        run.StartedAt = DateTime.UtcNow;
        run.CreatedAt = run.StartedAt;
        return run;
    }

    /// <summary>
    /// Sets <see cref="ReviewerActorId"/> once, and only while the run is started and <paramref name="actorId"/> is not white space. Otherwise does nothing.
    /// </summary>
    public void BindReviewer(string actorId)
    {
        if (Status != ReviewRunStatus.Started || string.IsNullOrWhiteSpace(actorId))
            return;
        ReviewerActorId ??= actorId.Trim();
    }

    /// <summary>
    /// Completes the run. <paramref name="artifactRef"/> is also stored as <see cref="TranscriptRef"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run is not <see cref="ReviewRunStatus.Started"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="artifactRef"/> is null or white space.</exception>
    public void Complete(string? findings, string artifactRef)
    {
        if (Status != ReviewRunStatus.Started)
            throw new InvalidOperationException($"Cannot complete a review run in status {Status}.");
        if (string.IsNullOrWhiteSpace(artifactRef))
            throw new ArgumentException("Artifact ref is required.", nameof(artifactRef));

        Findings = findings;
        ArtifactRef = artifactRef;
        TranscriptRef = artifactRef;
        Status = ReviewRunStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>Locks this run to the server-approved command before the workstation check is queued.</summary>
    public void SetCheckCommandHash(string commandHash)
    {
        if (Status != ReviewRunStatus.Started)
            throw new InvalidOperationException("Only a started review run can be bound to a check command.");
        if (!IsSha256(commandHash))
            throw new ArgumentException("A valid SHA-256 command hash is required.", nameof(commandHash));
        if (CheckCommandHash is not null)
            throw new InvalidOperationException("A review check command has already been assigned to this run.");
        CheckCommandHash = commandHash.ToLowerInvariant();
    }

    /// <summary>Completes a review proof tied to the exact task branch commit and Git tree that were checked.</summary>
    public void CompleteCheck(string? findings, string artifactRef, string checkedSourceSha, string checkedTreeSha)
    {
        if (string.IsNullOrWhiteSpace(CheckCommandHash)
            || !IsGitSha(checkedSourceSha)
            || !IsGitSha(checkedTreeSha))
            throw new InvalidOperationException("The approved command hash and checked Git source/tree SHAs are required.");
        Complete(findings, artifactRef);
        CheckedSourceSha = checkedSourceSha.ToLowerInvariant();
        CheckedTreeSha = checkedTreeSha.ToLowerInvariant();
    }

    /// <summary>
    /// Fails the run.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run is not <see cref="ReviewRunStatus.Started"/>.</exception>
    public void Fail(string? findings)
    {
        if (Status != ReviewRunStatus.Started)
            throw new InvalidOperationException($"Cannot fail a review run in status {Status}.");

        Findings = findings;
        Status = ReviewRunStatus.Failed;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// True only when completion proves the pinned command and exact checked Git source/tree.
    /// </summary>
    public bool IsCheckProof() =>
        Status == ReviewRunStatus.Completed
        && TargetRunId is not null
        && !string.IsNullOrWhiteSpace(ArtifactRef)
        && IsSha256(CheckCommandHash)
        && IsGitSha(CheckedSourceSha)
        && IsGitSha(CheckedTreeSha);

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>
    /// Starts an agent review and completes it immediately, using <paramref name="transcriptRef"/> as the artifact.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="transcriptRef"/> is null or white space.</exception>
    public static ReviewRun Create(Guid projectId, Guid taskId, string transcriptRef, string? reviewerActorId = null)
    {
        var run = Start(projectId, taskId, ReviewerType.Agent, reviewerActorId: reviewerActorId);
        run.Complete(null, transcriptRef);
        return run;
    }
}
