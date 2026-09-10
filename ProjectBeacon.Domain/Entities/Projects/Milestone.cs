namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class Milestone : Entity, IProjectScoped
{
    public Milestone() { }

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
        if (name is not null) Name = name;
        if (description is not null) Description = description;
        if (order is not null) Order = order.Value;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Delete()
    {
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
