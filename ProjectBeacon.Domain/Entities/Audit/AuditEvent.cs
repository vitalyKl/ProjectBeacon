namespace ProjectBeacon.Domain.Entities.Audit;

using System.Text;
using ProjectBeacon.Domain.Common;

/// <summary>
/// Append-only security and operator activity event. It stores identifiers and allowlisted
/// metadata only; chat/task/context/code content must never be attached.
/// </summary>
public sealed class AuditEvent : Entity
{
    public DateTime At { get; private set; }
    public AuditActorKind ActorKind { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public Guid? ActorTokenId { get; private set; }
    public Guid? ActorDeviceId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string TargetType { get; private set; } = string.Empty;
    public string? TargetId { get; private set; }
    public Guid? OrgId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public AuditOutcome Outcome { get; private set; }
    public string? Ip { get; private set; }
    public string? UserAgent { get; private set; }
    public string? TraceId { get; private set; }
    public string? DetailsJson { get; private set; }

    public static AuditEvent Create(
        DateTime atUtc,
        AuditActorKind actorKind,
        Guid? actorUserId,
        Guid? actorTokenId,
        Guid? actorDeviceId,
        string action,
        string targetType,
        string? targetId,
        Guid? orgId,
        Guid? projectId,
        AuditOutcome outcome,
        string? ip = null,
        string? userAgent = null,
        string? traceId = null,
        string? detailsJson = null)
    {
        if (atUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Audit event time must be UTC.", nameof(atUtc));
        if (string.IsNullOrWhiteSpace(action) || action.Length > 160)
            throw new ArgumentException("Audit action must contain 1 to 160 characters.", nameof(action));
        if (string.IsNullOrWhiteSpace(targetType) || targetType.Length > 80)
            throw new ArgumentException("Audit target type must contain 1 to 80 characters.", nameof(targetType));
        if (targetId?.Length > 128 || ip?.Length > 64 || userAgent?.Length > 512 || traceId?.Length > 128)
            throw new ArgumentException("Audit metadata exceeds its maximum length.");
        if (detailsJson is not null && Encoding.UTF8.GetByteCount(detailsJson) > 4096)
            throw new ArgumentException("Audit details must not exceed 4 KiB.", nameof(detailsJson));

        var auditEvent = Entity.New<AuditEvent>();
        auditEvent.At = atUtc;
        auditEvent.ActorKind = actorKind;
        auditEvent.ActorUserId = actorUserId;
        auditEvent.ActorTokenId = actorTokenId;
        auditEvent.ActorDeviceId = actorDeviceId;
        auditEvent.Action = action;
        auditEvent.TargetType = targetType;
        auditEvent.TargetId = targetId;
        auditEvent.OrgId = orgId;
        auditEvent.ProjectId = projectId;
        auditEvent.Outcome = outcome;
        auditEvent.Ip = ip;
        auditEvent.UserAgent = userAgent;
        auditEvent.TraceId = traceId;
        auditEvent.DetailsJson = detailsJson;
        return auditEvent;
    }
}