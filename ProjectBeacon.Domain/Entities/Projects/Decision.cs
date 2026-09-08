namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class Decision : Entity, ITenantScoped
{
    public Decision() { }

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

    public void Accept()
    {
        if (Status == DecisionStatus.Accepted)
            return;
        Status = DecisionStatus.Accepted;
    }

    public void Supersede(Decision replacement)
    {
        if (Status != DecisionStatus.Accepted)
            throw new InvalidOperationException("Only accepted decisions can be superseded.");
        Status = DecisionStatus.Superseded;
        SupersededById = replacement.Id;
    }

    public void Deprecate()
    {
        Status = DecisionStatus.Deprecated;
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
