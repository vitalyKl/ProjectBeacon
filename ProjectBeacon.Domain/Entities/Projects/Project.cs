namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using Identity;

public class Project : Entity, IOrgScoped
{
    public Project() { }

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid OrgId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public Org Org { get; private set; } = null!;
    public ICollection<TaskItem> Tasks { get; private set; } = [];
    public ICollection<Label> Labels { get; private set; } = [];
    public ICollection<ProjectMember> Members { get; private set; } = [];
    public ICollection<ProjectInvite> Invites { get; private set; } = [];
    public ICollection<ApiToken> ApiTokens { get; private set; } = [];
    public ICollection<Milestone> Milestones { get; private set; } = [];

    public static Project Create(string name, string? description, Guid orgId)
    {
        var project = Entity.New<Project>();
        project.Name = name;
        project.Description = description;
        project.OrgId = orgId;
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
