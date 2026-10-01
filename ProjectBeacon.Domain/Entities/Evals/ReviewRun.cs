namespace ProjectBeacon.Domain.Entities.Evals;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

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

    public static ReviewRun Create(Guid projectId, Guid taskId, string transcriptRef, string? reviewerActorId = null)
    {
        var run = Start(projectId, taskId, ReviewerType.Agent, reviewerActorId: reviewerActorId);
        run.Complete(null, transcriptRef);
        return run;
    }
}
