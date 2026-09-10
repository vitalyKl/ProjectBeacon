namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class LabelPath : Entity, IProjectScoped
{
    public LabelPath() { }

    public Guid LabelId { get; private set; }
    public string Path { get; private set; } = string.Empty;
    public Guid ProjectId { get; private set; }

    public Label Label { get; private set; } = null!;

    public static LabelPath Create(Guid labelId, string path, Guid projectId)
    {
        var entity = Entity.New<LabelPath>();
        entity.LabelId = labelId;
        entity.Path = path;
        entity.ProjectId = projectId;
        return entity;
    }
}
