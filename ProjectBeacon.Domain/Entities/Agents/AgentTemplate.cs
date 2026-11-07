namespace ProjectBeacon.Domain.Entities.Agents;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>User-owned binding of solo, planner, actor, and review backends. Applying it still writes the project's role bindings.</summary>
public class AgentTemplate : Entity
{
    public AgentTemplate() { }

    public Guid UserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public AgentRunMode Mode { get; private set; }
    public Guid? SoloBackendId { get; private set; }
    public Guid? PlannerBackendId { get; private set; }
    public Guid? ActorBackendId { get; private set; }
    public Guid? ReviewBackendId { get; private set; }
    public string PlannerInstructions { get; private set; } = string.Empty;
    public string ActorInstructions { get; private set; } = string.Empty;
    public string ReviewInstructions { get; private set; } = string.Empty;

    public static AgentTemplate Create(
        Guid userId,
        string name,
        AgentRunMode mode,
        Guid? soloBackendId,
        Guid? plannerBackendId,
        Guid? actorBackendId,
        Guid? reviewBackendId,
        string? plannerInstructions = null,
        string? actorInstructions = null,
        string? reviewInstructions = null)
    {
        var template = Entity.New<AgentTemplate>();
        template.UserId = userId;
        template.Name = name;
        template.Mode = mode;
        template.SoloBackendId = soloBackendId;
        template.PlannerBackendId = plannerBackendId;
        template.ActorBackendId = actorBackendId;
        template.ReviewBackendId = reviewBackendId;
        template.PlannerInstructions = plannerInstructions?.Trim() ?? string.Empty;
        template.ActorInstructions = actorInstructions?.Trim() ?? string.Empty;
        template.ReviewInstructions = reviewInstructions?.Trim() ?? string.Empty;
        return template;
    }

    public void Update(
        string name,
        AgentRunMode mode,
        Guid? soloBackendId,
        Guid? plannerBackendId,
        Guid? actorBackendId,
        Guid? reviewBackendId,
        string? plannerInstructions = null,
        string? actorInstructions = null,
        string? reviewInstructions = null)
    {
        Name = name;
        Mode = mode;
        SoloBackendId = soloBackendId;
        PlannerBackendId = plannerBackendId;
        ActorBackendId = actorBackendId;
        ReviewBackendId = reviewBackendId;
        PlannerInstructions = plannerInstructions?.Trim() ?? string.Empty;
        ActorInstructions = actorInstructions?.Trim() ?? string.Empty;
        ReviewInstructions = reviewInstructions?.Trim() ?? string.Empty;
    }
}
