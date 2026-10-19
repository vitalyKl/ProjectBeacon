namespace ProjectBeacon.Application.Identity;

using Application.Common;
using Domain.Entities.Identity;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Creates an org and adds the caller as owner. The org-scoped member insert runs on a
/// separate tenant instance keyed by the new org id so it succeeds for callers that hold
/// no tenant (fresh registrant, bootstrap admin). The circuit's scoped tenant is never
/// mutated, so concurrent operations on the same circuit keep their own tenant.
/// </summary>
public class CreateOrgHandler : ICommandHandler<CreateOrgCommand, Result<OrgDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ITenantContext _tenant;

    public CreateOrgHandler(IBeaconDbFactory dbFactory, ITenantContext tenant)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public async Task<Result<OrgDto>> HandleAsync(CreateOrgCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (command.Actor.UserId is { } userId
            && !await db.Users.AnyAsync(u => u.Id == userId, ct))
            return Result.Failure<OrgDto>("User not found.");

        var org = Org.Create(command.Request.Name, command.Request.Description);

        var window = new TenantContext();
        window.Assign(_tenant.ProjectId, org.Id, _tenant.Unscoped);
        await using var writeDb = _dbFactory.CreateDbContext(window);
        writeDb.Orgs.Add(org);
        if (command.Actor.UserId is { } ownerId)
            writeDb.OrgMembers.Add(OrgMember.Create(org.Id, ownerId, MemberRole.Owner));
        await writeDb.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(org));
    }

    private static OrgDto MapToDto(Org org) =>
        new(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt);
}
/// <summary>
/// Renames an org or changes its description.
/// </summary>
public class UpdateOrgHandler : ICommandHandler<UpdateOrgCommand, Result<OrgDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public UpdateOrgHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<OrgDto>> HandleAsync(UpdateOrgCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var org = await db.Orgs.FindAsync([command.Request.OrgId], ct);
        if (org is null)
            return Result.Failure<OrgDto>("Org not found.");

        org.Update(command.Request.Name, command.Request.Description);
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(org));
    }

    private static OrgDto MapToDto(Org org) =>
        new(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt);
}
/// <summary>
/// Loads one org.
/// </summary>
public class GetOrgHandler : ICommandHandler<GetOrgCommand, Result<OrgDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetOrgHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<OrgDto>> HandleAsync(GetOrgCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var org = await db.Orgs.FindAsync([command.Request.OrgId], ct);
        if (org is null)
            return Result.Failure<OrgDto>("Org not found.");

        return Result.Ok(MapToDto(org));
    }

    private static OrgDto MapToDto(Org org) =>
        new(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt);
}
/// <summary>
/// Lists orgs the caller can see.
/// </summary>
public class ListOrgsHandler : ICommandHandler<ListOrgsCommand, Result<IList<OrgDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListOrgsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<OrgDto>>> HandleAsync(ListOrgsCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var orgs = await db.Orgs
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new OrgDto(o.Id, o.Name, o.Description, o.CreatedAt, o.UpdatedAt))
            .ToListAsync(ct);

        return Result.Ok((IList<OrgDto>)orgs);
    }
}