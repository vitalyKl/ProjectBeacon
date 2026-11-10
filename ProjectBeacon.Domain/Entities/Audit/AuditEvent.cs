namespace ProjectBeacon.Domain.Entities.Audit;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectBeacon.Domain.Common;

/// <summary>
/// Append-only security and operator activity event. It stores identifiers and allowlisted
/// metadata only; chat/task/context/code content must never be attached.
/// </summary>
public sealed class AuditEvent : Entity
{
    private static readonly HashSet<string> AllowedDetailKeys = new(StringComparer.Ordinal)
    {
        "reasonCode", "permission", "authMethod", "source", "previousState",
        "newState", "count", "policyCode", "resultCode", "authenticatorReset"
    };

    private static readonly Regex DetailCodePattern =
        new("^[A-Za-z0-9._:-]{1,80}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        if (!Enum.IsDefined(actorKind))
            throw new ArgumentOutOfRangeException(nameof(actorKind));
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        if (string.IsNullOrWhiteSpace(action) || action.Length > 160)
            throw new ArgumentException("Audit action must contain 1 to 160 characters.", nameof(action));
        if (string.IsNullOrWhiteSpace(targetType) || targetType.Length > 80)
            throw new ArgumentException("Audit target type must contain 1 to 80 characters.", nameof(targetType));
        if (targetId?.Length > 128 || ip?.Length > 64 || userAgent?.Length > 512 || traceId?.Length > 128)
            throw new ArgumentException("Audit metadata exceeds its maximum length.");
        if (projectId.HasValue && !orgId.HasValue)
            throw new ArgumentException("A project-scoped audit event must also have an organization id.", nameof(projectId));
        if ((actorKind == AuditActorKind.ApiToken && !actorTokenId.HasValue)
            || (actorKind == AuditActorKind.Device && !actorDeviceId.HasValue))
            throw new ArgumentException("The actor identifier required by the actor kind is missing.");
        if ((actorUserId.HasValue ? 1 : 0) + (actorTokenId.HasValue ? 1 : 0) + (actorDeviceId.HasValue ? 1 : 0) > 1)
            throw new ArgumentException("An audit event may identify only one actor principal.");

        ValidateDetails(detailsJson);

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

    private static void ValidateDetails(string? detailsJson)
    {
        if (detailsJson is null)
            return;
        if (Encoding.UTF8.GetByteCount(detailsJson) > 4096)
            throw new ArgumentException("Audit details must not exceed 4 KiB.", nameof(detailsJson));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(detailsJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Audit details must be a JSON object containing allowlisted metadata only.", nameof(detailsJson), exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Audit details must be a JSON object.", nameof(detailsJson));

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!AllowedDetailKeys.Contains(property.Name))
                    throw new ArgumentException($"Audit detail key '{property.Name}' is not allowlisted.", nameof(detailsJson));

                var value = property.Value;
                if (value.ValueKind == JsonValueKind.String)
                {
                    var code = value.GetString();
                    if (code is null || !DetailCodePattern.IsMatch(code))
                        throw new ArgumentException($"Audit detail '{property.Name}' must be a short code, not free-form text.", nameof(detailsJson));
                }
                else if (value.ValueKind == JsonValueKind.Number)
                {
                    if (!value.TryGetInt64(out _))
                        throw new ArgumentException($"Audit detail '{property.Name}' must be an integer.", nameof(detailsJson));
                }
                else if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ArgumentException($"Audit detail '{property.Name}' must be a scalar value.", nameof(detailsJson));
                }
            }
        }
    }
}
