namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

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

    public static AgentTemplate Create(
        Guid userId,
        string name,
        AgentRunMode mode,
        Guid? soloBackendId,
        Guid? plannerBackendId,
        Guid? actorBackendId,
        Guid? reviewBackendId)
    {
        var template = Entity.New<AgentTemplate>();
        template.UserId = userId;
        template.Name = name;
        template.Mode = mode;
        template.SoloBackendId = soloBackendId;
        template.PlannerBackendId = plannerBackendId;
        template.ActorBackendId = actorBackendId;
        template.ReviewBackendId = reviewBackendId;
        return template;
    }

    public void Update(
        string name,
        AgentRunMode mode,
        Guid? soloBackendId,
        Guid? plannerBackendId,
        Guid? actorBackendId,
        Guid? reviewBackendId)
    {
        Name = name;
        Mode = mode;
        SoloBackendId = soloBackendId;
        PlannerBackendId = plannerBackendId;
        ActorBackendId = actorBackendId;
        ReviewBackendId = reviewBackendId;
    }
}
