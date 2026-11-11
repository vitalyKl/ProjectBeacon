namespace ProjectBeacon.Domain.Entities.Context;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>Project rule. Created Proposed. Only Active constraints belong in a compiled brief.</summary>
public class Constraint : Entity, IProjectScoped
{
    public Constraint() { }

    public int Revision { get; private set; } = 1;
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

    /// <summary>Updates all editable constraint content as one revision.</summary>
    public void UpdateContent(string body, string? scopePath)
    {
        var normalizedPath = scopePath ?? string.Empty;
        if (string.Equals(Body, body, StringComparison.Ordinal)
            && string.Equals(ScopePath, normalizedPath, StringComparison.Ordinal))
            return;

        Body = body;
        ScopePath = normalizedPath;
        Revision++;
    }

    public void UpdateBody(string body) => UpdateContent(body, ScopePath);

    /// <summary>Restores the editable body and scope path from a historical content snapshot.</summary>
    public void RestoreContentFromHistory(string body, string scopePath)
        => UpdateContent(body, scopePath);

    public void Activate()
    {
        if (Status == ConstraintStatus.Active)
            return;
        Status = ConstraintStatus.Active;
        Revision++;
    }

    public void Reject()
    {
        if (Status == ConstraintStatus.Rejected)
            return;
        Status = ConstraintStatus.Rejected;
        Revision++;
    }

    /// <summary>Restores content and lifecycle status from a historical snapshot as a new revision.</summary>
    public void RestoreFromHistory(string body, string scopePath, ConstraintStatus status)
    {
        var normalizedPath = scopePath ?? string.Empty;
        if (string.Equals(Body, body, StringComparison.Ordinal)
            && string.Equals(ScopePath, normalizedPath, StringComparison.Ordinal)
            && Status == status)
            return;

        Body = body;
        ScopePath = normalizedPath;
        Status = status;
        Revision++;
    }

    public void SetScopePath(string path) => UpdateContent(Body, path);
}
