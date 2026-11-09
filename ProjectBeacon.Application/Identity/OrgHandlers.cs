namespace ProjectBeacon.Application.Identity;

using Application.Common;
using Application.Audit;
using Application.Authorization;
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
    private readonly IAuditWriter? _auditWriter;

    public CreateOrgHandler(IBeaconDbFactory dbFactory, ITenantContext tenant, IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _auditWriter = auditWriter;
    }

    public async Task<Result<OrgDto>> HandleAsync(CreateOrgCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (command.Actor.UserId is { } userId
            && !await db.Users.AnyAsync(u => u.Id == userId, ct))
            return Result.Failure<OrgDto>("User not found.");

        var org = Org.Create(command.Request.Name, command.Request.Description);

        var window = new TenantContext();
        // An organization-level write must not inherit the previously selected project.
        window.Assign(null, org.Id, _tenant.Unscoped);
        await using var writeDb = _dbFactory.CreateDbContext(window);
        writeDb.Orgs.Add(org);
        if (command.Actor.UserId is { } ownerId)
            writeDb.OrgMembers.Add(OrgMember.Create(org.Id, ownerId, MemberRole.Owner));
        if (command.Actor.UserId is not null)
            _auditWriter?.Stage(writeDb, AuditEventFactory.ForActor(
                command.Actor, "org.created", "Org", org.Id.ToString(), org.Id, null,
                detailsJson: """{"newState":"Created"}"""));
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
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter? _auditWriter;

    public UpdateOrgHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
    }

    public async Task<Result<OrgDto>> HandleAsync(UpdateOrgCommand command, CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<OrgDto>();

        // Rebind to the requested organization only; a previously selected project must
        // never widen or redirect an organization-level mutation.
        var tenant = new TenantContext();
        tenant.Assign(null, command.Request.OrgId, unscoped: false);
        await using var db = _dbFactory.CreateDbContext(tenant);

        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Org, AuthAction.Administer,
            orgId: command.Request.OrgId, ct: ct);
        if (!permission.Success)
            return Result.Forbidden<OrgDto>();

        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Id == command.Request.OrgId, ct);
        if (org is null)
            return Result.Failure<OrgDto>("Org not found.");

        org.Update(command.Request.Name, command.Request.Description);
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            command.Actor, "org.updated", "Org", org.Id.ToString(), org.Id, null,
            detailsJson: """{"newState":"Updated"}"""));
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