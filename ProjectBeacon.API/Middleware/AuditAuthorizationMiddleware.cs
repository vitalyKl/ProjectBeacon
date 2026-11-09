namespace ProjectBeacon.API.Middleware;

using ProjectBeacon.API.Auth;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Records authenticated 403 responses as platform-wide authorization denials.
/// The response remains authoritative: audit persistence failure is logged but does not
/// change the caller's status or disclose details about the target tenant.
/// </summary>
public sealed class AuditAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuditWriter _auditWriter;
    private readonly ILogger<AuditAuthorizationMiddleware> _logger;

    public AuditAuthorizationMiddleware(
        RequestDelegate next,
        IBeaconDbFactory dbFactory,
        IAuditWriter auditWriter,
        ILogger<AuditAuthorizationMiddleware> logger)
    {
        _next = next;
        _dbFactory = dbFactory;
        _auditWriter = auditWriter;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        if (context.Response.StatusCode != StatusCodes.Status403Forbidden
            || context.User.Identity?.IsAuthenticated != true)
            return;

        try
        {
            var actor = ActorContextFactory.FromPrincipal(context.User);
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            var target = $"{context.Request.Method} {route}";
            if (target.Length > 128)
                target = target[..128];

            var unscopedTenant = new TenantContext();
            unscopedTenant.Assign(null, null, unscoped: true);
            await using var db = _dbFactory.CreateDbContext(unscopedTenant);
            _auditWriter.Stage(db, AuditEventFactory.ForActor(
                actor,
                "authorization.denied",
                "Endpoint",
                target,
                null,
                null,
                AuditOutcome.Denied,
                detailsJson: """{"reasonCode":"forbidden"}"""));
            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The request has already been cancelled; do not delay or replace its response.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not persist an authorization-denied audit event for trace {TraceId}.",
                context.TraceIdentifier);
        }
    }
}
