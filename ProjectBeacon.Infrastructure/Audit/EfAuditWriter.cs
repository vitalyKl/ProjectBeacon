namespace ProjectBeacon.Infrastructure.Audit;

using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;

/// <summary>EF implementation that stages audit rows on the caller's existing unit of work.</summary>
public sealed class EfAuditWriter : IAuditWriter
{
    public void Stage(IBeaconDb db, AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(auditEvent);

        if (!db.FilterUnscoped)
        {
            var orgMatch = auditEvent.OrgId.HasValue && auditEvent.OrgId == db.FilterOrgId;
            var projectMatch = auditEvent.ProjectId.HasValue && auditEvent.ProjectId == db.FilterProjectId;
            if (!orgMatch && !projectMatch)
                throw new UnauthorizedAccessException("The current tenant cannot write this audit event.");
        }

        db.AuditEvents.Add(auditEvent);
    }
}
