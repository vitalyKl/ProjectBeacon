namespace ProjectBeacon.Infrastructure.Audit;

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
    public async Task<AuditPage> ListAsync(AdminActor actor, AuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(query);

        if (!actor.HasPermission(OperatorPermission.AuditRead))
            throw new UnauthorizedAccessException("The operator does not have AuditRead permission.");
        if (query.Page < 1 || (long)(query.Page - 1) * query.PageSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(query.Page), "Audit page offset is outside the supported range.");
        if (query.PageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(query.PageSize));
        if ((query.FromUtc.HasValue && query.FromUtc.Value.Kind != DateTimeKind.Utc)
            || (query.ToUtc.HasValue && query.ToUtc.Value.Kind != DateTimeKind.Utc))
            throw new ArgumentException("Audit query timestamps must be UTC.", nameof(query));
        if (query.FromUtc.HasValue && query.ToUtc.HasValue && query.FromUtc.Value > query.ToUtc.Value)
            throw new ArgumentException("The audit query start must not be after its end.", nameof(query));
        if (query.Action?.Length > 160)
            throw new ArgumentException("Audit action filter is too long.", nameof(query));

        using (TenantScope.EnterUnscoped())
        {
            await using var db = dbFactory.CreateDbContext();
            IQueryable<AuditEvent> events = db.AuditEvents.AsNoTracking();

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
            if (query.Outcome.HasValue)
                events = events.Where(e => e.Outcome == query.Outcome.Value);

            var total = await events.LongCountAsync(ct);
            var skip = checked((query.Page - 1) * query.PageSize);
            var rows = await events
                .OrderByDescending(e => e.At)
                .ThenByDescending(e => e.Id)
                .Skip(skip)
                .Take(query.PageSize)
                .Select(e => new AuditEntry(
                    e.Id, e.At, e.ActorKind, e.ActorUserId, e.ActorTokenId, e.ActorDeviceId,
                    e.Action, e.TargetType, e.TargetId, e.OrgId, e.ProjectId, e.Outcome,
                    e.Ip, e.UserAgent, e.TraceId, e.DetailsJson))
                .ToListAsync(ct);

            return new AuditPage(rows, total, query.Page, query.PageSize);
        }
    }
}
