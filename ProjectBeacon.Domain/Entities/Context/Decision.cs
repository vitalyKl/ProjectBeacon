namespace ProjectBeacon.Domain.Entities.Context;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// Project decision. Created Proposed. <see cref="Supersede"/> requires Accepted.
/// <see cref="Deprecate"/> does not check the current status.
/// </summary>
public class Decision : Entity, IProjectScoped
{
    public Decision() { }

    public int Revision { get; private set; } = 1;
    public string Title { get; private set; } = string.Empty;
    public string Context { get; private set; } = string.Empty;
    public string DecisionBody { get; private set; } = string.Empty;
    public string? Consequences { get; private set; }
    public DecisionStatus Status { get; private set; }
    public Guid? SupersededById { get; private set; }
    public Decision? SupersededBy { get; private set; }
    public Guid ProjectId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public ICollection<DecisionTask> RelatedTasks { get; private set; } = [];

    public static Decision Create(string title, string context, string decisionBody, Guid projectId, string? consequences = null)
    {
        var d = Entity.New<Decision>();
        d.Title = title;
        d.Context = context;
        d.DecisionBody = decisionBody;
        d.Consequences = consequences;
        d.Status = DecisionStatus.Proposed;
        d.ProjectId = projectId;
        d.CreatedAt = DateTime.UtcNow;
        return d;
    }

    public void UpdateContent(string title, string context, string decisionBody, string? consequences)
    {
        if (string.Equals(Title, title, StringComparison.Ordinal)
            && string.Equals(Context, context, StringComparison.Ordinal)
            && string.Equals(DecisionBody, decisionBody, StringComparison.Ordinal)
            && string.Equals(Consequences, consequences, StringComparison.Ordinal))
            return;

        Title = title;
        Context = context;
        DecisionBody = decisionBody;
        Consequences = consequences;
        Revision++;
    }

    public void Accept()
    {
        if (Status == DecisionStatus.Accepted)
            return;
        Status = DecisionStatus.Accepted;
        Revision++;
    }

    public void Supersede(Decision replacement)
    {
        if (Status != DecisionStatus.Accepted)
            throw new InvalidOperationException("Only accepted decisions can be superseded.");
        Status = DecisionStatus.Superseded;
        SupersededById = replacement.Id;
        Revision++;
    }

    public void Deprecate()
    {
        if (Status == DecisionStatus.Deprecated)
            return;
        Status = DecisionStatus.Deprecated;
        Revision++;
    }

    /// <summary>Restores content and lifecycle status from a historical snapshot as a new revision.</summary>
    public void RestoreFromHistory(
        string title,
        string context,
        string decisionBody,
        string? consequences,
        DecisionStatus status,
        Guid? supersededById)
    {
        if (string.Equals(Title, title, StringComparison.Ordinal)
            && string.Equals(Context, context, StringComparison.Ordinal)
            && string.Equals(DecisionBody, decisionBody, StringComparison.Ordinal)
            && string.Equals(Consequences, consequences, StringComparison.Ordinal)
            && Status == status
            && SupersededById == supersededById)
            return;

        Title = title;
        Context = context;
        DecisionBody = decisionBody;
        Consequences = consequences;
        Status = status;
        SupersededById = supersededById;
        Revision++;
    }

    public void AddRelatedTask(Guid taskId)
    {
        var existing = RelatedTasks.FirstOrDefault(d => d.TaskId == taskId);
        if (existing is not null)
            return;
        RelatedTasks.Add(DecisionTask.Create(Id, taskId));
    }

    public void RemoveRelatedTask(Guid taskId)
    {
        var existing = RelatedTasks.FirstOrDefault(d => d.TaskId == taskId);
        if (existing is not null)
            RelatedTasks.Remove(existing);
    }
}
