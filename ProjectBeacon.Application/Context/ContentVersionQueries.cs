namespace ProjectBeacon.Application.Context;

using System.Text.Json;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Application.Proposals;
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

/// <summary>
/// A physically deleted ContextSection or Milestone that can still be recreated from
/// an earlier immutable snapshot. The tombstone revision is the required current revision
/// for the restore command.
/// </summary>
public sealed record DeletedContentTargetDto(
    ContentTargetKind TargetKind,
    Guid TargetId,
    string Title,
    int DeletedRevision,
    Guid SourceVersionId,
    string SourceContentJson,
    DateTime DeletedAt);

/// <summary>Lists the latest restorable tombstones for a project member.</summary>
public sealed class ListDeletedContentTargetsHandler
{
    private const int ScanLimit = 10_000;
    private readonly IBeaconDbFactory _dbFactory;

    public ListDeletedContentTargetsHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IReadOnlyList<DeletedContentTargetDto>>> HandleAsync(
        Guid projectId,
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (projectId == Guid.Empty)
            return Result.Failure<IReadOnlyList<DeletedContentTargetDto>>("ProjectId is required.");
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<IReadOnlyList<DeletedContentTargetDto>>(
                "Only a project member can inspect deleted content history.");

        await using var db = _dbFactory.CreateDbContext();
        if (!await ProposalProjectAuthorization.CanWriteAsync(db, actor, projectId, ct))
            return Result.Forbidden<IReadOnlyList<DeletedContentTargetDto>>(
                "Project membership is required to inspect deleted content history.");

        var versions = await db.ContentVersions
            .AsNoTracking()
            .Where(version => version.ProjectId == projectId
                && (version.TargetKind == ContentTargetKind.ContextSection
                    || version.TargetKind == ContentTargetKind.Milestone))
            .OrderByDescending(version => version.At)
            .ThenByDescending(version => version.Revision)
            .Take(ScanLimit)
            .ToListAsync(ct);

        var deleted = new List<DeletedContentTargetDto>();
        foreach (var group in versions.GroupBy(version => (version.TargetKind, version.TargetId)))
        {
            var ordered = group.OrderByDescending(version => version.Revision).ToArray();
            var latest = ordered[0];
            if (!IsDeleted(latest.ContentJson))
                continue;

            var source = ordered.Skip(1).FirstOrDefault(version => !IsDeleted(version.ContentJson));
            if (source is null)
                continue;

            deleted.Add(new DeletedContentTargetDto(
                latest.TargetKind,
                latest.TargetId,
                ReadTitle(source.ContentJson, latest.TargetKind, latest.TargetId),
                latest.Revision,
                source.Id,
                source.ContentJson,
                latest.At));
        }

        return Result.Ok((IReadOnlyList<DeletedContentTargetDto>)deleted
            .OrderByDescending(item => item.DeletedAt)
            .Take(100)
            .ToArray());
    }

    private static bool IsDeleted(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("deleted", out var deleted)
                && deleted.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ReadTitle(string json, ContentTargetKind kind, Guid targetId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var key = kind == ContentTargetKind.ContextSection ? "title" : "name";
            if (document.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!;
        }
        catch (JsonException)
        {
            // Fall back to the immutable target id if the historical snapshot is damaged.
        }

        return $"{kind} {targetId.ToString("N")[..8]}";
    }
}

