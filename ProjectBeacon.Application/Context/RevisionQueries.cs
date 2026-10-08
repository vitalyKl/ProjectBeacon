namespace ProjectBeacon.Application.Context;

using Application.Common;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// A saved compile: time, token estimate, and hash. The markdown itself is not on this row.
/// </summary>
public record ContextRevisionDto(Guid Id, DateTime CreatedAt, int TokenEstimate, string CompiledHash);
/// <summary>
/// Lists saved brief revisions for the project.
/// </summary>
public class ListContextRevisionsHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListContextRevisionsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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