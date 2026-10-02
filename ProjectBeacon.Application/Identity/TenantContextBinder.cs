namespace ProjectBeacon.Application.Identity;

using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Authorization;

public sealed class TenantContextBinder
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ITenantContext _tenant;

    public TenantContextBinder(IBeaconDbFactory dbFactory, ITenantContext tenant)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public async Task BindUserAsync(ActorContext actor, CancellationToken ct = default)
    {
        if (_tenant.ProjectId is not null || _tenant.Unscoped)
            return;
        if (actor.UserId is null)
            return;

        await using var db = _dbFactory.CreateDbContext();
        var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(db, actor.UserId.Value, actor.IsAdmin, actor.ProjectId, ct);
        _tenant.Assign(projectId, orgId, unscoped: false);
    }
}
