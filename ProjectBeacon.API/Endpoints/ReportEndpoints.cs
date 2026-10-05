namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

using Application.Reports;
using Microsoft.AspNetCore.Mvc;

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects/{projectId:guid}/reports", Generate).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/reports", List).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/reports/{reportId:guid}", Get).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/reports/context-cost/{taskId:guid}", ContextCost).RequireAuthorization().DisableAntiforgery().RequireHuman();
        return app;
    }

    private static async Task<IResult> Generate(Guid projectId, [FromBody] GenerateReportBody? body, HttpContext ctx, GenerateReportHandler handler)
    {
        var actor = ctx.GetActor();
        var createdByType = string.IsNullOrWhiteSpace(body?.CreatedByType) ? "user" : body.CreatedByType;
        var createdById = body?.CreatedById ?? actor.UserId?.ToString() ?? string.Empty;
        var result = await handler.HandleAsync(new GenerateReportCommand(
            new GenerateReportRequest(projectId, createdByType, createdById)));
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/reports/{result.Value!.Id}", result.Value)
            : result.FromResult();
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
            : result.FromResult(404);
    }

    private static async Task<IResult> ContextCost(Guid taskId, ContextCostReportHandler handler)
    {
        var result = await handler.HandleAsync(new GetContextCostReportRequest(taskId));
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

    public record GenerateReportBody(string? CreatedByType, string? CreatedById);
}
