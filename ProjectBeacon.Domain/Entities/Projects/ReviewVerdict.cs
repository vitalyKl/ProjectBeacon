namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class ReviewVerdict : Entity, IProjectScoped
{
    public ReviewVerdict() { }

    public Guid TaskId { get; private set; }
    public Guid? SubtaskId { get; private set; }
    public Guid ProjectId { get; private set; }
    public ReviewVerdictKind Kind { get; private set; }
    public string Note { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }

    public TaskItem Task { get; private set; } = null!;

    public static ReviewVerdict Create(Guid taskId, Guid projectId, ReviewVerdictKind kind, string note, Guid? subtaskId = null)
    {
        if (string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("Verdict note cannot be empty.", nameof(note));

        var verdict = Entity.New<ReviewVerdict>();
        verdict.TaskId = taskId;
        verdict.ProjectId = projectId;
        verdict.Kind = kind;
        verdict.Note = note;
        verdict.SubtaskId = subtaskId;
        verdict.CreatedAt = DateTime.UtcNow;
        return verdict;
    }
}
