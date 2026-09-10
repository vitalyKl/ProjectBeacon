namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class Constraint : Entity, IProjectScoped
{
    public Constraint() { }

    public string Body { get; private set; } = string.Empty;
    public string ScopePath { get; private set; } = string.Empty;
    public ConstraintKind Kind { get; private set; }
    public ConstraintStatus Status { get; private set; }
    public Guid ProjectId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static Constraint Create(string body, ConstraintKind kind, Guid projectId, string? scopePath = null)
    {
        var constraint = Entity.New<Constraint>();
        constraint.Body = body;
        constraint.Kind = kind;
        constraint.ProjectId = projectId;
        constraint.ScopePath = scopePath ?? string.Empty;
        constraint.Status = ConstraintStatus.Proposed;
        constraint.CreatedAt = DateTime.UtcNow;
        return constraint;
    }

    public void Activate()
    {
        if (Status == ConstraintStatus.Active)
            return;
        Status = ConstraintStatus.Active;
    }

    public void Reject()
    {
        if (Status == ConstraintStatus.Rejected)
            return;
        Status = ConstraintStatus.Rejected;
    }

    public void SetScopePath(string path)
    {
        ScopePath = path ?? string.Empty;
    }
}
