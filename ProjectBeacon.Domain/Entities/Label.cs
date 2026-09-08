namespace ProjectBeacon.Domain.Entities;

using ProjectBeacon.Domain.Common;

public class Label : Entity
{
    public Label() { }

    public string Name { get; private set; } = string.Empty;
    public string Color { get; private set; } = "#000000";
    public Guid ProjectId { get; private set; }

    public Project Project { get; private set; } = null!;
    public ICollection<TaskItem> Tasks { get; private set; } = [];

    public static Label Create(string name, string color, Guid projectId)
    {
        var label = Entity.New<Label>();
        label.Name = name;
        label.Color = color;
        label.ProjectId = projectId;
        return label;
    }
}
