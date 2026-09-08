namespace ProjectBeacon.Domain.Entities;

using ProjectBeacon.Domain.Common;

public class Project : Entity
{
    public Project() { }

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public ICollection<TaskItem> Tasks { get; private set; } = [];
    public ICollection<Label> Labels { get; private set; } = [];

    public static Project Create(string name, string? description = null)
    {
        var project = Entity.New<Project>();
        project.Name = name;
        project.Description = description;
        project.CreatedAt = DateTime.UtcNow;
        return project;
    }

    public void Update(string? name = null, string? description = null)
    {
        if (name is not null) Name = name;
        if (description is not null) Description = description;
        UpdatedAt = DateTime.UtcNow;
    }
}
