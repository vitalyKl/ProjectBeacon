namespace ProjectBeacon.Domain.Entities.Identity;

using ProjectBeacon.Domain.Common;
using Projects = ProjectBeacon.Domain.Entities.Projects;

public class Org : Entity
{
    public Org() { }

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public ICollection<OrgMember> Members { get; private set; } = [];
    public ICollection<OrgInvite> Invites { get; private set; } = [];
    public ICollection<Projects.Project> Projects { get; private set; } = [];

    public static Org Create(string name, string? description = null)
    {
        var org = Entity.New<Org>();
        org.Name = name;
        org.Description = description;
        org.CreatedAt = DateTime.UtcNow;
        return org;
    }

    public void Update(string? name = null, string? description = null)
    {
        if (name is not null) Name = name;
        if (description is not null) Description = description;
        UpdatedAt = DateTime.UtcNow;
    }
}
