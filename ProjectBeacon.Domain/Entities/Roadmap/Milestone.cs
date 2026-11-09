namespace ProjectBeacon.Domain.Entities.Roadmap;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Entities.Tasks;

/// <summary>Roadmap milestone. <see cref="Close"/> sets <see cref="ClosedAt"/>; <see cref="Reopen"/> clears it.</summary>
public class Milestone : Entity, IProjectScoped
{
    public Milestone() { }

    public int Revision { get; private set; } = 1;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid ProjectId { get; private set; }
    public int Order { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }

    public Project Project { get; private set; } = null!;
    public ICollection<TaskItem> Tasks { get; private set; } = [];

    public static Milestone Create(string name, string? description, Guid projectId, int order)
    {
        var milestone = Entity.New<Milestone>();
        milestone.Name = name;
        milestone.Description = description;
        milestone.ProjectId = projectId;
        milestone.Order = order;
        milestone.CreatedAt = DateTime.UtcNow;
        return milestone;
    }

    public void Update(string? name = null, string? description = null, int? order = null)
    {
        var contentChanged =
            (name is not null && !string.Equals(Name, name, StringComparison.Ordinal))
            || (description is not null && !string.Equals(Description, description, StringComparison.Ordinal))
            || (order is not null && Order != order.Value);
        if (name is not null) Name = name;
        if (description is not null) Description = description;
        if (order is not null) Order = order.Value;
        if (contentChanged)
            Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Restores milestone content, including a nullable description, from history.</summary>
    public void RestoreContentFromHistory(string name, string? description, int order)
    {
        if (string.Equals(Name, name, StringComparison.Ordinal)
            && string.Equals(Description, description, StringComparison.Ordinal)
            && Order == order)
            return;

        Name = name;
        Description = description;
        Order = order;
        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Delete()
    {
        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Close()
    {
        ClosedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Reopen()
    {
        ClosedAt = null;
        UpdatedAt = DateTime.UtcNow;
    }
}
