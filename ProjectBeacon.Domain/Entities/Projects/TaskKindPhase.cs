namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class TaskKindPhase : Entity
{
    public TaskKindPhase() { }

    public Guid TaskKindId { get; private set; }
    public int SortOrder { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Instruction { get; private set; } = string.Empty;
    public bool FanOut { get; private set; }
    public Guid? ModelBackendId { get; private set; }

    public static TaskKindPhase Create(
        Guid taskKindId,
        int sortOrder,
        string? key,
        string title,
        string? instruction,
        bool fanOut,
        Guid? modelBackendId)
    {
        var phase = Entity.New<TaskKindPhase>();
        phase.TaskKindId = taskKindId;
        phase.SortOrder = sortOrder;
        phase.Key = key?.Trim() ?? string.Empty;
        phase.Title = title.Trim();
        phase.Instruction = instruction?.Trim() ?? string.Empty;
        phase.FanOut = fanOut;
        phase.ModelBackendId = modelBackendId;
        return phase;
    }
}
