namespace ProjectBeacon.Application.Audit;

using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;

/// <summary>
/// Stages an immutable event on the same IBeaconDb unit of work as the business mutation.
/// The caller must save that context once; the event is therefore committed or rolled back
/// with the operation it describes.
/// </summary>
public interface IAuditWriter
{
    void Stage(IBeaconDb db, AuditEvent auditEvent);
}

/// <summary>Read-only, paged view of audit history. Caller must obtain AdminActor through IAdminGate.</summary>
public interface IAuditReader
{
    Task<AuditPage> ListAsync(AdminActor actor, AuditQuery query, CancellationToken ct = default);
}

public sealed record AuditQuery(
    Guid? OrgId = null,
    Guid? ProjectId = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? Action = null,
    AuditActorKind? ActorKind = null,
    Guid? ActorUserId = null,
    AuditOutcome? Outcome = null,
    int Page = 1,
    int PageSize = 50,
    Guid? ActorTokenId = null,
    Guid? ActorDeviceId = null,
    string? TargetType = null,
    string? TargetId = null);

public sealed record AuditEntry(
    Guid Id,
    DateTime At,
    AuditActorKind ActorKind,
    Guid? ActorUserId,
    Guid? ActorTokenId,
    Guid? ActorDeviceId,
    string Action,
    string TargetType,
    string? TargetId,
    Guid? OrgId,
    Guid? ProjectId,
    AuditOutcome Outcome,
    string? Ip,
    string? UserAgent,
    string? TraceId,
    string? DetailsJson);

public sealed record AuditPage(
    IReadOnlyList<AuditEntry> Items,
    long TotalCount,
    int Page,
    int PageSize);
