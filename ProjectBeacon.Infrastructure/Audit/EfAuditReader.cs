namespace ProjectBeacon.Infrastructure.Audit;

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Infrastructure.Data;

/// <summary>
/// Operator-only audit reader. It enters an explicit unscoped database scope only after
/// receiving the non-forgeable AdminActor produced by IAdminGate.
/// </summary>
public sealed class EfAuditReader(IBeaconDbFactory dbFactory) : IAuditReader
{
    private const int MaxExportRows = 50_000;

    public async Task<AuditPage> ListAsync(AdminActor actor, AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(query);

        if (!actor.HasPermission(OperatorPermission.AuditRead))
            throw new UnauthorizedAccessException("The operator does not have AuditRead permission.");
        ValidateFilters(query);
        if (query.Page < 1)
            throw new ArgumentOutOfRangeException(nameof(query.Page));
        if (query.PageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(query.PageSize));

        using (TenantScope.EnterUnscoped())
        {
            await using var db = dbFactory.CreateDbContext();
            var events = ApplyFilters(db.AuditEvents.AsNoTracking(), query);
            var total = await events.LongCountAsync(ct);
            var skip = checked((query.Page - 1) * query.PageSize);
            var rows = await Project(events)
                .Skip(skip)
                .Take(query.PageSize)
                .ToListAsync(ct);

            return new AuditPage(rows, total, query.Page, query.PageSize);
        }
    }

    public async IAsyncEnumerable<AuditEntry> StreamAsync(
        AdminActor actor,
        AuditQuery query,
        int maxRows,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(query);

        if (!actor.HasPermission(OperatorPermission.AuditExport))
            throw new UnauthorizedAccessException("The operator does not have AuditExport permission.");
        ValidateFilters(query);
        if (maxRows is < 1 or > MaxExportRows)
            throw new ArgumentOutOfRangeException(nameof(maxRows), $"Audit export is limited to {MaxExportRows} rows.");

        using (TenantScope.EnterUnscoped())
        {
            await using var db = dbFactory.CreateDbContext();
            var rows = Project(ApplyFilters(db.AuditEvents.AsNoTracking(), query))
                .Take(maxRows + 1)
                .AsAsyncEnumerable();

            await foreach (var row in rows.WithCancellation(ct))
                yield return row;
        }
    }

    private static IQueryable<AuditEvent> ApplyFilters(IQueryable<AuditEvent> events, AuditQuery query)
    {
        if (query.OrgId.HasValue)
            events = events.Where(e => e.OrgId == query.OrgId);
        if (query.ProjectId.HasValue)
            events = events.Where(e => e.ProjectId == query.ProjectId);
        if (query.FromUtc.HasValue)
            events = events.Where(e => e.At >= query.FromUtc.Value);
        if (query.ToUtc.HasValue)
            events = events.Where(e => e.At <= query.ToUtc.Value);
        if (!string.IsNullOrWhiteSpace(query.Action))
            events = events.Where(e => e.Action == query.Action);
        if (query.ActorKind.HasValue)
            events = events.Where(e => e.ActorKind == query.ActorKind.Value);
        if (query.ActorUserId.HasValue)
            events = events.Where(e => e.ActorUserId == query.ActorUserId);
        if (query.ActorTokenId.HasValue)
            events = events.Where(e => e.ActorTokenId == query.ActorTokenId);
        if (query.ActorDeviceId.HasValue)
            events = events.Where(e => e.ActorDeviceId == query.ActorDeviceId);
        if (query.Outcome.HasValue)
            events = events.Where(e => e.Outcome == query.Outcome.Value);

        return events;
    }

    private static IQueryable<AuditEntry> Project(IQueryable<AuditEvent> events) =>
        events
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .Select(e => new AuditEntry(
                e.Id, e.At, e.ActorKind, e.ActorUserId, e.ActorTokenId, e.ActorDeviceId,
                e.Action, e.TargetType, e.TargetId, e.OrgId, e.ProjectId, e.Outcome,
                e.Ip, e.UserAgent, e.TraceId, e.DetailsJson));

    private static void ValidateFilters(AuditQuery query)
    {
        if ((query.FromUtc.HasValue && query.FromUtc.Value.Kind != DateTimeKind.Utc)
            || (query.ToUtc.HasValue && query.ToUtc.Value.Kind != DateTimeKind.Utc))
            throw new ArgumentException("Audit query timestamps must be UTC.", nameof(query));
        if (query.FromUtc.HasValue && query.ToUtc.HasValue && query.FromUtc.Value > query.ToUtc.Value)
            throw new ArgumentException("The audit query start must not be after its end.", nameof(query));
        if (query.Action?.Length > 160)
            throw new ArgumentException("Audit action filter is too long.", nameof(query));
    }
}
