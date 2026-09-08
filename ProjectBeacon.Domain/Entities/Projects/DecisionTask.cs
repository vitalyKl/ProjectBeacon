namespace ProjectBeacon.Domain.Entities.Projects;

public class DecisionTask
{
    public DecisionTask() { }

    public Guid DecisionId { get; private set; }
    public Guid TaskId { get; private set; }
    public Decision Decision { get; private set; } = null!;
    public TaskItem Task { get; private set; } = null!;

    public static DecisionTask Create(Guid decisionId, Guid taskId)
    {
        var entity = new DecisionTask();
        entity.DecisionId = decisionId;
        entity.TaskId = taskId;
        return entity;
    }
}
