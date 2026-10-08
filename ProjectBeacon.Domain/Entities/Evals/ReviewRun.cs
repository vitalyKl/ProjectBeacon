namespace ProjectBeacon.Domain.Entities.Evals;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// A project-scoped review of a task. Starts as <see cref="ReviewRunStatus.Started"/>.
/// <see cref="IsCheckProof"/> requires completion, a target run, and an artifact ref.
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
    /// True when the run is completed, <see cref="TargetRunId"/> is set, and <see cref="ArtifactRef"/> is not white space.
    /// </summary>
    public bool IsCheckProof() =>
        Status == ReviewRunStatus.Completed
        && TargetRunId is not null
        && !string.IsNullOrWhiteSpace(ArtifactRef);

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
