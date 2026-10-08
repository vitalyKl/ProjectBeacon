namespace ProjectBeacon.Domain.Common;

/// <summary>
/// Marker for tenant-scoped entities. Device entities are user-owned and do not implement this.
/// The scope key is <see cref="IOrgScoped.OrgId"/> or <see cref="IProjectScoped.ProjectId"/>.
/// </summary>
public interface ITenantScoped { }

/// <summary>
/// Org-scoped tenant. Implementers are filtered by <see cref="OrgId"/>.
/// </summary>
public interface IOrgScoped : ITenantScoped
{
    Guid OrgId { get; }
}

/// <summary>
/// Project-scoped tenant. Implementers are filtered by <see cref="ProjectId"/>.
/// </summary>
public interface IProjectScoped : ITenantScoped
{
    Guid ProjectId { get; }
}
