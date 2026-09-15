namespace ProjectBeacon.Application.Identity;

using Application.Common;
using Domain.Entities.Identity;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class CreateOrgHandler : ICommandHandler<CreateOrgCommand, Result<OrgDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public CreateOrgHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<OrgDto>> HandleAsync(CreateOrgCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (command.Request.CreatedByUserId is { } userId
            && !await db.Users.AnyAsync(u => u.Id == userId, ct))
            return Result.Failure<OrgDto>("User not found.");

        var org = Org.Create(command.Request.Name, command.Request.Description);

        db.Orgs.Add(org);
        if (command.Request.CreatedByUserId is { } ownerId)
            db.OrgMembers.Add(OrgMember.Create(org.Id, ownerId, MemberRole.Owner));
        await db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(org));
    }

    private static OrgDto MapToDto(Org org) =>
        new(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt);
}

public class UpdateOrgHandler : ICommandHandler<UpdateOrgCommand, Result<OrgDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public UpdateOrgHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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

public class GetOrgHandler : ICommandHandler<GetOrgCommand, Result<OrgDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetOrgHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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

public class ListOrgsHandler : ICommandHandler<ListOrgsCommand, Result<IList<OrgDto>>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListOrgsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

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