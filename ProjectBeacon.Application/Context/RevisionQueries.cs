namespace ProjectBeacon.Application.Context;

using Application.Common;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record ContextRevisionDto(Guid Id, DateTime CreatedAt, int TokenEstimate, string CompiledHash);

public class ListContextRevisionsHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListContextRevisionsHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ContextRevisionDto>>> HandleAsync(CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var items = await db.ContextRevisions
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .Select(r => new ContextRevisionDto(r.Id, r.CreatedAt, r.TokenEstimate, r.CompiledHash))
            .ToListAsync(ct);
        return Result.Ok((IList<ContextRevisionDto>)items);
    }
}