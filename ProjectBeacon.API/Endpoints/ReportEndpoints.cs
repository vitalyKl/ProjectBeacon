namespace ProjectBeacon.API.Endpoints;

using Application.Reports;
using Microsoft.AspNetCore.Mvc;

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects/{projectId:guid}/reports", Generate).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}/reports", List).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}/reports/{reportId:guid}", Get).RequireAuthorization().DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> Generate(Guid projectId, [FromBody] GenerateReportBody? body, HttpContext ctx, GenerateReportHandler handler)
    {
        var createdByType = string.IsNullOrWhiteSpace(body?.CreatedByType) ? "user" : body.CreatedByType;
        var createdById = body?.CreatedById
                          ?? ctx.User.FindFirst("sub")?.Value
                          ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? string.Empty;
        var result = await handler.HandleAsync(new GenerateReportCommand(
            new GenerateReportRequest(projectId, createdByType, createdById)));
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/reports/{result.Value.Id}", result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> List(Guid projectId, ListReportsHandler handler)
    {
        var result = await handler.HandleAsync(new ListReportsRequest(projectId));
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> Get(Guid projectId, Guid reportId, GetReportHandler handler)
    {
        var result = await handler.HandleAsync(new GetReportRequest(projectId, reportId));
        return result.Success
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    public record GenerateReportBody(string? CreatedByType, string? CreatedById);
}
