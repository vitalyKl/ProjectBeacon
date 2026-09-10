namespace ProjectBeacon.Application.Identity;

using System.Security.Claims;
using Infrastructure.Data;

public sealed class TenantContextBinder
{
    private readonly BeaconDbContext _db;
    private readonly ITenantContext _tenant;

    public TenantContextBinder(BeaconDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task BindUserAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (_tenant.ProjectId is not null || _tenant.Unscoped)
            return;
        if (user.Identity?.IsAuthenticated != true)
            return;
        if (!Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            return;

        var isAdmin = bool.TryParse(user.FindFirst("isAdmin")?.Value, out var flag) && flag;
        var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(_db, userId, isAdmin, ct);
        _tenant.Assign(projectId, orgId, unscoped: false);
    }
}
