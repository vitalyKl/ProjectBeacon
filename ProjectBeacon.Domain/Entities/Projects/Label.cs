namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class Label : Entity, IProjectScoped
{
    public Label() { }

    public string Name { get; private set; } = string.Empty;
    public string Color { get; private set; } = "#000000";
    public string PathPrefix { get; private set; } = string.Empty;
    public Guid ProjectId { get; private set; }

    public Project Project { get; private set; } = null!;
    public ICollection<TaskItem> Tasks { get; private set; } = [];
    public ICollection<LabelPath> Paths { get; private set; } = [];

    public static Label Create(string name, string color, Guid projectId, string? pathPrefix = null)
    {
        var label = Entity.New<Label>();
        label.Name = name;
        label.Color = color;
        label.ProjectId = projectId;
        label.PathPrefix = pathPrefix ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(label.PathPrefix))
            label.Paths.Add(LabelPath.Create(label.Id, label.PathPrefix, projectId));
        return label;
    }

    public void SetPathPrefix(string pathPrefix)
    {
        PathPrefix = pathPrefix ?? string.Empty;
    }

    public void AddPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (Paths.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;
        Paths.Add(LabelPath.Create(Id, path, ProjectId));
        if (string.IsNullOrWhiteSpace(PathPrefix))
            PathPrefix = path;
    }

    public IEnumerable<string> AllPrefixes()
    {
        if (!string.IsNullOrWhiteSpace(PathPrefix))
            yield return PathPrefix;
        foreach (var path in Paths)
        {
            if (!string.IsNullOrWhiteSpace(path.Path))
                yield return path.Path;
        }
    }
}
