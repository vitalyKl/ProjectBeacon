namespace ProjectBeacon.Domain.Common;

/// <summary>
/// Org-scoped tenant. Implementers are filtered by <see cref="OrgId"/>.
/// Device entities are user-owned and implement neither scope.
/// </summary>
public interface IOrgScoped
{
    Guid OrgId { get; }
}

/// <summary>
/// Project-scoped tenant. Implementers are filtered by <see cref="ProjectId"/>.
/// </summary>
public interface IProjectScoped
{
    Guid ProjectId { get; }
}
