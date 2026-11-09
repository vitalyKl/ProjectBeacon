namespace ProjectBeacon.Application.Context;

using Application.Common;
using Application.Persistence;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>A durable snapshot of one revisioned content target.</summary>
public sealed record ContentVersionDto(
    Guid Id,
    ContentTargetKind TargetKind,
    Guid TargetId,
    int Revision,
    string ContentJson,
    string? ChangedByType,
    string? ChangedById,
    DateTime At);

/// <summary>Reads version history for one project-scoped content target.</summary>
public sealed class ListContentVersionsHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListContentVersionsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ContentVersionDto>>> HandleAsync(
        Guid projectId,
        ContentTargetKind targetKind,
        Guid targetId,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (projectId == Guid.Empty || targetId == Guid.Empty)
            return Result.Failure<IList<ContentVersionDto>>("ProjectId and TargetId are required.");

        await using var db = _dbFactory.CreateDbContext();
        var items = await db.ContentVersions
            .Where(v => v.ProjectId == projectId && v.TargetKind == targetKind && v.TargetId == targetId)
            .OrderByDescending(v => v.Revision)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(v => new ContentVersionDto(
                v.Id, v.TargetKind, v.TargetId, v.Revision, v.ContentJson,
                v.ChangedByType, v.ChangedById, v.At))
            .ToListAsync(ct);

        return Result.Ok((IList<ContentVersionDto>)items);
    }
}
