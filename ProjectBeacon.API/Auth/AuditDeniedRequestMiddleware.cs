namespace ProjectBeacon.API.Auth;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;

/// <summary>
/// Records HTTP 403 responses as platform-scoped security events.
/// Request bodies and query-string values are intentionally never captured.
/// </summary>
public sealed class AuditDeniedRequestMiddleware(
    RequestDelegate next,
    ILogger<AuditDeniedRequestMiddleware> logger)
{
    private const string Details = """{"reasonCode":"http.403"}""";

    public async Task InvokeAsync(
        HttpContext context,
        IBeaconDbFactory dbFactory,
        IAuditWriter auditWriter)
    {
        await next(context);

        if (context.Response.StatusCode != StatusCodes.Status403Forbidden
            || !context.Request.Path.StartsWithSegments("/v1"))
            return;

        try
        {
            var actor = ActorContextFactory.FromPrincipal(context.User);
            var actorKind = actor.Type switch
            {
                ActorType.Human when actor.UserId.HasValue => AuditActorKind.Human,
                ActorType.ApiToken when actor.TokenId.HasValue => AuditActorKind.ApiToken,
                ActorType.Device when actor.DeviceId.HasValue => AuditActorKind.Device,
                ActorType.Worker => AuditActorKind.Worker,
                _ => AuditActorKind.System
            };

            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "/v1/unknown";
            var targetId = $"{context.Request.Method} {route}";
            if (targetId.Length > 128)
                targetId = targetId[..128];

            var auditEvent = AuditEvent.Create(
                DateTime.UtcNow,
                actorKind,
                actorKind == AuditActorKind.Human ? actor.UserId : null,
                actorKind == AuditActorKind.ApiToken ? actor.TokenId : null,
                actorKind == AuditActorKind.Device ? actor.DeviceId : null,
                "security.authorization.denied",
                "HttpEndpoint",
                targetId,
                orgId: null,
                projectId: null,
                AuditOutcome.Denied,
                ip: Limit(context.Connection.RemoteIpAddress?.ToString(), 64),
                userAgent: Limit(context.Request.Headers.UserAgent.ToString(), 512),
                traceId: Limit(context.TraceIdentifier, 128),
                detailsJson: Details);

            var globalTenant = new TenantContext();
            globalTenant.Assign(null, null, unscoped: true);
            await using var db = dbFactory.CreateDbContext(globalTenant);
            auditWriter.Stage(db, auditEvent);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A logging failure must never change the original authorization response.
            logger.LogWarning(ex, "Could not persist an audit record for a denied HTTP request.");
        }
    }

    private static string? Limit(string? value, int maximum) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= maximum ? value : value[..maximum];
}