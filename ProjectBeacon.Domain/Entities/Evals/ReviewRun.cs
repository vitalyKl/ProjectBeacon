namespace ProjectBeacon.Domain.Entities.Evals;

using ProjectBeacon.Domain.Common;

public sealed class ReviewRun : Entity, IProjectScoped
{
    public ReviewRun() { }

    public Guid ProjectId { get; private set; }
    public Guid TaskId { get; private set; }
    public string TranscriptRef { get; private set; } = string.Empty;
    public string? ReviewerActorId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static ReviewRun Create(Guid projectId, Guid taskId, string transcriptRef, string? reviewerActorId = null)
    {
        var run = Entity.New<ReviewRun>();
        run.ProjectId = projectId;
        run.TaskId = taskId;
        run.TranscriptRef = transcriptRef;
        run.ReviewerActorId = reviewerActorId;
        run.CreatedAt = DateTime.UtcNow;
        return run;
    }
}
