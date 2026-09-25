namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class TaskKind : Entity
{
    public TaskKind() { }

    public Guid UserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsBuiltIn { get; private set; }
    public Guid? DecisionBackendId { get; private set; }
    public Guid? WorkerBackendId { get; private set; }

    public static TaskKind Create(Guid userId, string name, bool isBuiltIn, Guid? decisionBackendId, Guid? workerBackendId)
    {
        var kind = Entity.New<TaskKind>();
        kind.UserId = userId;
        kind.Name = name.Trim();
        kind.IsBuiltIn = isBuiltIn;
        kind.DecisionBackendId = decisionBackendId;
        kind.WorkerBackendId = workerBackendId;
        return kind;
    }

    public void Update(string name, Guid? decisionBackendId, Guid? workerBackendId)
    {
        if (!IsBuiltIn)
            Name = name.Trim();
        DecisionBackendId = decisionBackendId;
        WorkerBackendId = workerBackendId;
    }
}
