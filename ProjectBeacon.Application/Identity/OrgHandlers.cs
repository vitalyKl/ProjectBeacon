namespace ProjectBeacon.Application.Identity;

using Application.Admin;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Domain.Entities.Identity;
using Domain.Enums;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Creates organizations only through the verified platform-operator boundary. The new
/// organization is intentionally not joined by the operator; its owner enters through an invite.
/// </summary>
public class CreateOrgHandler : ICommandHandler<CreateOrgCommand, Result<OrgDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAdminGate _adminGate;
    private readonly IAuditWriter? _auditWriter;

    public CreateOrgHandler(IBeaconDbFactory dbFactory, IAdminGate adminGate, IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _adminGate = adminGate;
        _auditWriter = auditWriter;
    }

    public async Task<Result<OrgDto>> HandleAsync(CreateOrgCommand command, CancellationToken ct = default)
    {
        var access = await _adminGate.RequireAsync(
            command.Actor, OperatorPermission.OrganizationsManage, ct: ct);
        if (!access.Success || access.Value is null)
            return Result.Forbidden<OrgDto>();

        if (string.IsNullOrWhiteSpace(command.Request.Name))
            return Result.Failure<OrgDto>("Organization name is required.");

        var org = Org.Create(command.Request.Name.Trim(), command.Request.Description?.Trim());
        var tenant = OrgTenant(org.Id);
        await using var db = _dbFactory.CreateDbContext(tenant);
        db.Orgs.Add(org);

        var operatorActor = new ActorContext(
            ActorType.Human, access.Value.UserId, true, null, null, null, org.Id, ApiTokenCapability.None);
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            operatorActor, "org.created", "Org", org.Id.ToString(), org.Id, null,
            detailsJson: """{"newState":"Active"}"""));

        await db.SaveChangesAsync(ct);
        return Result.Ok(MapToDto(org));
    }

    private static TenantContext OrgTenant(Guid orgId)
    {
        var tenant = new TenantContext();
        tenant.Assign(null, orgId, unscoped: false);
        return tenant;
    }

    private static OrgDto MapToDto(Org org) =>
        new(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt);
}

/// <summary>
/// Renames an organization after checking its actual tenant membership or operator permission.
/// </summary>
public class UpdateOrgHandler : ICommandHandler<UpdateOrgCommand, Result<OrgDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAdminGate _adminGate;
    private readonly IAuditWriter? _auditWriter;

    public UpdateOrgHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAdminGate adminGate,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _adminGate = adminGate;
        _auditWriter = auditWriter;
    }

    public async Task<Result<OrgDto>> HandleAsync(UpdateOrgCommand command, CancellationToken ct = default)
    {
        var orgId = command.Request.OrgId;
        if (command.Actor.IsAdmin)
        {
            var access = await _adminGate.RequireAsync(
                command.Actor, OperatorPermission.OrganizationsManage, ct: ct);
            if (!access.Success)
                return Result.Forbidden<OrgDto>();
        }
        else
        {
            await using var authorizationDb = _dbFactory.CreateDbContext(OrgTenant(orgId));
            var access = await _authorization.CanAsync(
                authorizationDb, command.Actor, ResourceType.Org, AuthAction.Administer,
                orgId: orgId, ct: ct);
            if (!access.Success)
                return Result.Failure<OrgDto>("Org not found.");
        }

        await using var db = _dbFactory.CreateDbContext(OrgTenant(orgId));
        var org = await db.Orgs.FirstOrDefaultAsync(item => item.Id == orgId, ct);
        if (org is null)
            return Result.Failure<OrgDto>("Org not found.");

        org.Update(command.Request.Name, command.Request.Description);
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            command.Actor, "org.updated", "Org", org.Id.ToString(), org.Id, null,
            detailsJson: """{"newState":"Updated"}"""));
        await db.SaveChangesAsync(ct);
        return Result.Ok(MapToDto(org));
    }

    private static TenantContext OrgTenant(Guid orgId)
    {
        var tenant = new TenantContext();
        tenant.Assign(null, orgId, unscoped: false);
        return tenant;
    }

    private static OrgDto MapToDto(Org org) =>
        new(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt);
}

/// <summary>
/// Loads an organization only for a member or a verified platform operator.
/// </summary>
public class GetOrgHandler : ICommandHandler<GetOrgCommand, Result<OrgDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAdminGate _adminGate;

    public GetOrgHandler(IBeaconDbFactory dbFactory, IAdminGate adminGate)
    {
        _dbFactory = dbFactory;
        _adminGate = adminGate;
    }

    public async Task<Result<OrgDto>> HandleAsync(GetOrgCommand command, CancellationToken ct = default)
    {
        var actor = command.Actor;
        if (actor.IsAdmin)
        {
            var access = await _adminGate.RequireAsync(actor, OperatorPermission.OrganizationsRead, ct: ct);
            if (!access.Success)
                return Result.Failure<OrgDto>("Org not found.");
        }
        else if (actor.UserId is not { } userId)
        {
            return Result.Failure<OrgDto>("Org not found.");
        }

        var tenant = new TenantContext();
        tenant.Assign(null, command.Request.OrgId, unscoped: false);
        await using var db = _dbFactory.CreateDbContext(tenant);

        if (!actor.IsAdmin
            && !await db.OrgMembers.AnyAsync(
                member => member.OrgId == command.Request.OrgId && member.UserId == actor.UserId, ct))
            return Result.Failure<OrgDto>("Org not found.");

        var org = await db.Orgs.FirstOrDefaultAsync(item => item.Id == command.Request.OrgId, ct);
        return org is null
            ? Result.Failure<OrgDto>("Org not found.")
            : Result.Ok(MapToDto(org));
    }

    private static OrgDto MapToDto(Org org) =>
        new(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt);
}

/// <summary>
/// Lists all organizations for verified platform operators and only memberships for customers.
/// The cross-tenant membership lookup is explicit, unscoped, and constrained to the caller's user id.
/// </summary>
public class ListOrgsHandler : ICommandHandler<ListOrgsCommand, Result<IList<OrgDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAdminGate _adminGate;

    public ListOrgsHandler(IBeaconDbFactory dbFactory, IAdminGate adminGate)
    {
        _dbFactory = dbFactory;
        _adminGate = adminGate;
    }

    public async Task<Result<IList<OrgDto>>> HandleAsync(ListOrgsCommand command, CancellationToken ct = default)
    {
        if (command.Actor.IsAdmin)
        {
            var access = await _adminGate.RequireAsync(
                command.Actor, OperatorPermission.OrganizationsRead, ct: ct);
            if (!access.Success)
                return Result.Forbidden<IList<OrgDto>>();

            await using var adminDb = _dbFactory.CreateDbContext();
            var all = await adminDb.Orgs
                .OrderByDescending(org => org.CreatedAt)
                .Select(org => new OrgDto(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt))
                .ToListAsync(ct);
            return Result.Ok((IList<OrgDto>)all);
        }

        if (command.Actor.UserId is not { } userId)
            return Result.Forbidden<IList<OrgDto>>();

        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        await using var lookupDb = _dbFactory.CreateDbContext(tenant);
        var orgIds = await lookupDb.OrgMembers
            .Where(member => member.UserId == userId)
            .Select(member => member.OrgId)
            .Distinct()
            .ToListAsync(ct);

        var orgs = await lookupDb.Orgs
            .Where(org => orgIds.Contains(org.Id))
            .OrderByDescending(org => org.CreatedAt)
            .Select(org => new OrgDto(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt))
            .ToListAsync(ct);
        return Result.Ok((IList<OrgDto>)orgs);
    }
}