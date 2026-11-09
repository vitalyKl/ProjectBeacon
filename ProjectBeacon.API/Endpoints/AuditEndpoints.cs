namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Domain.Entities.Audit;

/// <summary>Platform-operator audit history endpoints.</summary>
public static class AuditEndpoints
{
    /// <summary>
    /// Maps the operator-only, bounded audit history query. The handler revalidates the
    /// persisted operator account and permission before the reader enters an unscoped query.
    /// </summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/admin/audit", List)
            .RequireAuthorization()
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> List(
        HttpContext context,
        IAdminGate adminGate,
        IAuditReader auditReader,
        Guid? orgId,
        Guid? projectId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string? action,
        AuditActorKind? actorKind,
        Guid? actorUserId,
        AuditOutcome? outcome,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var admin = await adminGate.RequireAsync(
            context.GetActor(),
            OperatorPermission.AuditRead,
            ct: ct);
        if (!admin.Success)
            return admin.FromResult();

        var query = new AuditQuery(
            OrgId: orgId,
            ProjectId: projectId,
            FromUtc: fromUtc?.UtcDateTime,
            ToUtc: toUtc?.UtcDateTime,
            Action: action,
            ActorKind: actorKind,
            ActorUserId: actorUserId,
            Outcome: outcome,
            Page: page ?? 1,
            PageSize: pageSize ?? 50);

        try
        {
            var result = await auditReader.ListAsync(admin.Value!, query, ct);
            return Results.Ok(result);
        }
        catch (ArgumentException exception)
        {
            return ProblemResults.Bad(exception.Message);
        }
        catch (OverflowException)
        {
            return ProblemResults.Bad("The requested audit page is outside the supported range.");
        }
    }
}
