namespace ProjectBeacon.Domain.Entities.Identity;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;
using Projects = ProjectBeacon.Domain.Entities.Projects;

/// <summary>
/// An organization. Not itself org-scoped; members, invites, and projects hang off <see cref="Id"/>.
/// </summary>
public class Org : Entity
{
    public Org() { }

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public OrganizationLifecycleStatus LifecycleStatus { get; private set; } = OrganizationLifecycleStatus.Active;

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

    /// <summary>
    /// Applies each non-null argument and stamps <see cref="UpdatedAt"/>. Null arguments are left unchanged.
    /// </summary>
    public void Update(string? name = null, string? description = null)
    {
        if (name is not null) Name = name;
        if (description is not null) Description = description;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Changes the organization lifecycle. Closing is terminal; an active organization must
    /// first be suspended, and a closed organization can never be reactivated.
    /// </summary>
    public void ChangeLifecycleStatus(OrganizationLifecycleStatus target, DateTime changedAtUtc)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target));
        if (target == LifecycleStatus)
            return;

        var allowed = (LifecycleStatus, target) switch
        {
            (OrganizationLifecycleStatus.Active, OrganizationLifecycleStatus.Suspended) => true,
            (OrganizationLifecycleStatus.Suspended, OrganizationLifecycleStatus.Active) => true,
            (OrganizationLifecycleStatus.Suspended, OrganizationLifecycleStatus.Closed) => true,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException(
                $"Organization lifecycle transition '{LifecycleStatus}' -> '{target}' is not allowed.");

        LifecycleStatus = target;
        UpdatedAt = DateTime.SpecifyKind(changedAtUtc, DateTimeKind.Utc);
    }
}
