namespace ProjectBeacon.Application.Identity;

using System.Security.Claims;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public sealed class TenantContextBinder
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly ITenantContext _tenant;

    public TenantContextBinder(IDbContextFactory<BeaconDbContext> dbFactory, ITenantContext tenant)
    {
        _dbFactory = dbFactory;
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
        var preferred = ParseClaim(user, "project_id");
        await using var db = _dbFactory.CreateDbContext();
        var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(db, userId, isAdmin, preferred, ct);
        _tenant.Assign(projectId, orgId, unscoped: false);
    }

    private static Guid? ParseClaim(ClaimsPrincipal user, string claimType)
        => Guid.TryParse(user.FindFirst(claimType)?.Value, out var value) ? value : null;
}
