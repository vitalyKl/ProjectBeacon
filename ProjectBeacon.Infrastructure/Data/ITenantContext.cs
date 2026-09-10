namespace ProjectBeacon.Infrastructure.Data;

public interface ITenantContext
{
    Guid? ProjectId { get; }
    Guid? OrgId { get; }
    bool Unscoped { get; }
    void Assign(Guid? projectId, Guid? orgId, bool unscoped);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? ProjectId { get; private set; }
    public Guid? OrgId { get; private set; }
    public bool Unscoped { get; private set; }

    public void Assign(Guid? projectId, Guid? orgId, bool unscoped)
    {
        ProjectId = projectId;
        OrgId = orgId;
        Unscoped = unscoped;
    }
}
