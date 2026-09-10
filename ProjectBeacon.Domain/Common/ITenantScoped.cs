namespace ProjectBeacon.Domain.Common;

public interface ITenantScoped { }

public interface IOrgScoped : ITenantScoped
{
    Guid OrgId { get; }
}

public interface IProjectScoped : ITenantScoped
{
    Guid ProjectId { get; }
}
