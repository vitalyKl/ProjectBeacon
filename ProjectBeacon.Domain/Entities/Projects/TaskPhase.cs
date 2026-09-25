namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class TaskPhase : Entity, IProjectScoped
{
    public TaskPhase() { }

    public Guid TaskId { get; private set; }
    public Guid ProjectId { get; private set; }
    public int SortOrder { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Instruction { get; private set; } = string.Empty;
    public bool FanOut { get; private set; }
    public Guid? ModelBackendId { get; private set; }
    public TaskPhaseStatus Status { get; private set; }

    public static TaskPhase CopyFrom(TaskKindPhase source, Guid taskId, Guid projectId, Guid? modelBackendId)
    {
        var phase = Entity.New<TaskPhase>();
        phase.TaskId = taskId;
        phase.ProjectId = projectId;
        phase.SortOrder = source.SortOrder;
        phase.Key = source.Key;
        phase.Title = source.Title;
        phase.Instruction = source.Instruction;
        phase.FanOut = source.FanOut;
        phase.ModelBackendId = modelBackendId;
        phase.Status = TaskPhaseStatus.Pending;
        return phase;
    }

    public void SetModel(Guid? modelBackendId) => ModelBackendId = modelBackendId;

    public void SetStatus(TaskPhaseStatus status) => Status = status;
}
