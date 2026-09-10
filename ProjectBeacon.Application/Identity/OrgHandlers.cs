namespace ProjectBeacon.Application.Identity;

using Application.Common;
using Domain.Entities.Identity;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public class CreateOrgHandler : ICommandHandler<CreateOrgCommand, Result<OrgDto>>
{
    private readonly BeaconDbContext _db;

    public CreateOrgHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<OrgDto>> HandleAsync(CreateOrgCommand command, CancellationToken ct = default)
    {
        var org = Org.Create(command.Request.Name, command.Request.Description);

        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(org));
    }

    private static OrgDto MapToDto(Org org) =>
        new(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt);
}

public class UpdateOrgHandler : ICommandHandler<UpdateOrgCommand, Result<OrgDto>>
{
    private readonly BeaconDbContext _db;

    public UpdateOrgHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<OrgDto>> HandleAsync(UpdateOrgCommand command, CancellationToken ct = default)
    {
        var org = await _db.Orgs.FindAsync([command.Request.OrgId], ct);
        if (org is null)
            return Result.Failure<OrgDto>("Org not found.");

        org.Update(command.Request.Name, command.Request.Description);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(MapToDto(org));
    }

    private static OrgDto MapToDto(Org org) =>
        new(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt);
}

public class GetOrgHandler : ICommandHandler<GetOrgCommand, Result<OrgDto>>
{
    private readonly BeaconDbContext _db;

    public GetOrgHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<OrgDto>> HandleAsync(GetOrgCommand command, CancellationToken ct = default)
    {
        var org = await _db.Orgs.FindAsync([command.Request.OrgId], ct);
        if (org is null)
            return Result.Failure<OrgDto>("Org not found.");

        return Result.Ok(MapToDto(org));
    }

    private static OrgDto MapToDto(Org org) =>
        new(org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt);
}

public class ListOrgsHandler : ICommandHandler<ListOrgsCommand, Result<IList<OrgDto>>>
{
    private readonly BeaconDbContext _db;

    public ListOrgsHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<IList<OrgDto>>> HandleAsync(ListOrgsCommand command, CancellationToken ct = default)
    {
        var orgs = await _db.Orgs
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new OrgDto(o.Id, o.Name, o.Description, o.CreatedAt, o.UpdatedAt))
            .ToListAsync(ct);

        return Result.Ok((IList<OrgDto>)orgs);
    }
}
