namespace ProjectBeacon.API.Endpoints;

using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API;
using ProjectBeacon.API.Auth;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Domain.Entities.Audit;

/// <summary>Platform-operator audit query and export endpoints.</summary>
public static class AuditEndpoints
{
    private const int ExportPageSize = 200;
    private const int MaximumExportRows = 10_000;

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/admin/audit", List)
            .RequireAuthorization()
            .RequireRateLimiting("auth-read")
            .RequireHuman();

        app.MapGet("/v1/admin/audit/export.csv", ExportCsv)
            .RequireAuthorization()
            .RequireRateLimiting("auth-read")
            .RequireHuman();

        return app;
    }

    private static async Task<IResult> List(
        HttpContext ctx,
        IAdminGate adminGate,
        IAuditReader reader,
        [FromQuery] Guid? orgId,
        [FromQuery] Guid? projectId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? action,
        [FromQuery] AuditActorKind? actorKind,
        [FromQuery] Guid? actorUserId,
        [FromQuery] AuditOutcome? outcome,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        var access = await adminGate.RequireAsync(ctx.GetActor(), OperatorPermission.AuditRead, ct: ct);
        if (!access.Success)
            return access.FromResult();

        var query = new AuditQuery(orgId, projectId, fromUtc, toUtc, action, actorKind,
            actorUserId, outcome, page ?? 1, pageSize ?? 50);
        try
        {
            return Results.Ok(await reader.ListAsync(access.Value!, query, ct));
        }
        catch (ArgumentException ex)
        {
            return ProblemResults.Bad(ex.Message);
        }
    }

    private static async Task<IResult> ExportCsv(
        HttpContext ctx,
        IAdminGate adminGate,
        IAuditReader reader,
        [FromQuery] Guid? orgId,
        [FromQuery] Guid? projectId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? action,
        [FromQuery] AuditActorKind? actorKind,
        [FromQuery] Guid? actorUserId,
        [FromQuery] AuditOutcome? outcome,
        CancellationToken ct)
    {
        var access = await adminGate.RequireAsync(ctx.GetActor(), OperatorPermission.AuditExport, ct: ct);
        if (!access.Success)
            return access.FromResult();

        var filter = new AuditQuery(orgId, projectId, fromUtc, toUtc, action, actorKind,
            actorUserId, outcome, Page: 1, PageSize: ExportPageSize);
        var rows = new List<AuditEntry>();
        long total;
        try
        {
            var first = await reader.ListAsync(access.Value!, filter, ct);
            total = first.TotalCount;
            rows.AddRange(first.Items.Take(MaximumExportRows));

            var pages = (int)Math.Ceiling(
                (double)Math.Min(total, MaximumExportRows) / ExportPageSize);
            for (var currentPage = 2; currentPage <= pages; currentPage++)
            {
                var next = await reader.ListAsync(access.Value!, filter with { Page = currentPage }, ct);
                rows.AddRange(next.Items.Take(MaximumExportRows - rows.Count));
            }
        }
        catch (ArgumentException ex)
        {
            return ProblemResults.Bad(ex.Message);
        }

        ctx.Response.Headers["X-Audit-Export-Truncated"] = total > rows.Count ? "true" : "false";
        var csv = BuildCsv(rows);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        var fileName = $"projectbeacon-audit-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.csv";
        return Results.File(bytes, "text/csv; charset=utf-8", fileName);
    }

    private static string BuildCsv(IEnumerable<AuditEntry> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("id,atUtc,actorKind,actorUserId,actorTokenId,actorDeviceId,action,targetType,targetId,orgId,projectId,outcome,ip,userAgent,traceId,detailsJson");
        foreach (var row in rows)
        {
            var values = new string?[]
            {
                row.Id.ToString(),
                row.At.ToString("O", CultureInfo.InvariantCulture),
                row.ActorKind.ToString(),
                row.ActorUserId?.ToString(),
                row.ActorTokenId?.ToString(),
                row.ActorDeviceId?.ToString(),
                row.Action,
                row.TargetType,
                row.TargetId,
                row.OrgId?.ToString(),
                row.ProjectId?.ToString(),
                row.Outcome.ToString(),
                row.Ip,
                row.UserAgent,
                row.TraceId,
                row.DetailsJson
            };
            builder.AppendLine(string.Join(",", values.Select(EscapeCsv)));
        }
        return builder.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}