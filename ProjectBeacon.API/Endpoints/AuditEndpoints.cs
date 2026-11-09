namespace ProjectBeacon.API.Endpoints;

using System.Globalization;
using System.Text;
using ProjectBeacon.API;
using ProjectBeacon.API.Auth;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Domain.Entities.Audit;

/// <summary>Platform-operator audit history endpoints.</summary>
public static class AuditEndpoints
{
    private const int MaxExportRows = 10_000;

    /// <summary>
    /// Maps the operator-only, bounded audit history query and CSV export.
    /// Each handler revalidates the persisted operator account and permission before an unscoped read.
    /// </summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/admin/audit", List)
            .RequireAuthorization()
            .DisableAntiforgery();
        app.MapGet("/v1/admin/audit/export", Export)
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

        var query = BuildQuery(
            orgId, projectId, fromUtc, toUtc, action, actorKind, actorUserId, outcome,
            page ?? 1, pageSize ?? 50);
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

    private static async Task<IResult> Export(
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
        CancellationToken ct)
    {
        var admin = await adminGate.RequireAsync(
            context.GetActor(),
            OperatorPermission.AuditExport,
            ct: ct);
        if (!admin.Success)
            return admin.FromResult();

        var query = BuildQuery(
            orgId, projectId, fromUtc, toUtc, action, actorKind, actorUserId, outcome,
            page: 1, pageSize: 200);
        var rows = new List<AuditEntry>();
        try
        {
            await foreach (var entry in auditReader.StreamAsync(admin.Value!, query, MaxExportRows, ct))
            {
                rows.Add(entry);
                if (rows.Count > MaxExportRows)
                    return Results.Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "Audit export is too large",
                        detail: $"Narrow the filters to export no more than {MaxExportRows} events.");
            }
        }
        catch (ArgumentException exception)
        {
            return ProblemResults.Bad(exception.Message);
        }

        var csv = new StringBuilder();
        AppendCsvRow(csv,
            "AtUtc", "ActorKind", "ActorUserId", "ActorTokenId", "ActorDeviceId",
            "Action", "TargetType", "TargetId", "OrgId", "ProjectId", "Outcome",
            "Ip", "UserAgent", "TraceId", "DetailsJson");
        foreach (var entry in rows)
        {
            AppendCsvRow(csv,
                DateTime.SpecifyKind(entry.At, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
                entry.ActorKind.ToString(),
                entry.ActorUserId?.ToString(),
                entry.ActorTokenId?.ToString(),
                entry.ActorDeviceId?.ToString(),
                entry.Action,
                entry.TargetType,
                entry.TargetId,
                entry.OrgId?.ToString(),
                entry.ProjectId?.ToString(),
                entry.Outcome.ToString(),
                entry.Ip,
                entry.UserAgent,
                entry.TraceId,
                entry.DetailsJson);
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv.ToString());
        var fileName = $"projectbeacon-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        return Results.File(bytes, "text/csv; charset=utf-8", fileName);
    }

    private static AuditQuery BuildQuery(
        Guid? orgId,
        Guid? projectId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string? action,
        AuditActorKind? actorKind,
        Guid? actorUserId,
        AuditOutcome? outcome,
        int page,
        int pageSize) =>
        new(
            OrgId: orgId,
            ProjectId: projectId,
            FromUtc: fromUtc?.UtcDateTime,
            ToUtc: toUtc?.UtcDateTime,
            Action: action,
            ActorKind: actorKind,
            ActorUserId: actorUserId,
            Outcome: outcome,
            Page: page,
            PageSize: pageSize);

    private static void AppendCsvRow(StringBuilder csv, params string?[] values)
    {
        csv.AppendJoin(',', values.Select(EscapeCsv));
        csv.Append("\r\n");
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        var firstMeaningful = value.FirstOrDefault(character => !char.IsWhiteSpace(character));
        if (firstMeaningful is '=' or '+' or '-' or '@')
            value = "'" + value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
