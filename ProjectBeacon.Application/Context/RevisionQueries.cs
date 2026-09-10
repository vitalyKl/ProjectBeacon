namespace ProjectBeacon.Application.Context;

using Application.Common;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record ContextRevisionDto(Guid Id, DateTime CreatedAt, int TokenEstimate, string CompiledHash);

public class ListContextRevisionsHandler
{
    private readonly BeaconDbContext _db;

    public ListContextRevisionsHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<IList<ContextRevisionDto>>> HandleAsync(CancellationToken ct = default)
    {
        var items = await _db.ContextRevisions
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .Select(r => new ContextRevisionDto(r.Id, r.CreatedAt, r.TokenEstimate, r.CompiledHash))
            .ToListAsync(ct);
        return Result.Ok((IList<ContextRevisionDto>)items);
    }
}
