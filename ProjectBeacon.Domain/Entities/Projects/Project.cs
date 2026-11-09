namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Entities.Roadmap;
using ProjectBeacon.Domain.Entities.Tasks;
using Identity;

/// <summary>Org-scoped project. It has no single working-tree root; that lives on <see cref="ProjectRuntime"/>.</summary>
public class Project : Entity, IOrgScoped
{
    public Project() { }

    public int Revision { get; private set; } = 1;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? ReviewCheckCommand { get; private set; }
    /// <summary>Encrypted JSON containing this project's MCP server definitions and credentials.</summary>
    public string McpServersCipher { get; private set; } = string.Empty;
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

    public void SetMcpServersCipher(string cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        McpServersCipher = cipher;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Restores the nullable description from a historical content snapshot.</summary>
    public void RestoreDescriptionFromHistory(string? description)
    {
        if (string.Equals(Description, description, StringComparison.Ordinal))
            return;

        Description = description;
        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Update(string? name = null, string? description = null, string? reviewCheckCommand = null)
    {
        if (name is not null) Name = name;
        if (description is not null)
        {
            if (!string.Equals(Description, description, StringComparison.Ordinal))
                Revision++;
            Description = description;
        }
        if (reviewCheckCommand is not null)
            ReviewCheckCommand = string.IsNullOrWhiteSpace(reviewCheckCommand) ? null : reviewCheckCommand.Trim();
        UpdatedAt = DateTime.UtcNow;
    }
}
