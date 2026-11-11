namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

using Application.Reports;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Report HTTP endpoints.
/// </summary>
public static class ReportEndpoints
{
    /// <summary>
    /// Maps <c>POST /v1/projects/{projectId:guid}/reports</c>, <c>GET /v1/projects/{projectId:guid}/reports</c>, <c>GET /v1/projects/{projectId:guid}/reports/{reportId:guid}</c>, and <c>GET /v1/reports/context-cost/{taskId:guid}</c> with <c>RequireHuman</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects/{projectId:guid}/reports", Generate).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/reports", List).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/reports/{reportId:guid}", Get).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/reports/context-cost/{taskId:guid}", ContextCost).RequireAuthorization().DisableAntiforgery().RequireHuman();
        return app;
    }

    private static async Task<IResult> Generate(Guid projectId, HttpContext ctx, GenerateReportHandler handler)
    {
        // Attribution is security metadata, not report content. Never accept it from the
        // request body: a caller must not be able to forge another user's authorship.
        var actor = ctx.GetActor();
        if (!actor.IsHuman || actor.UserId is null)
            return ProblemResults.Unauthorized();

        var result = await handler.HandleAsync(new GenerateReportCommand(
            new GenerateReportRequest(projectId), actor));
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/reports/{result.Value!.Id}", result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> List(Guid projectId, HttpContext ctx, ListReportsHandler handler)
    {
        var result = await handler.HandleAsync(new ListReportsRequest(projectId, ctx.GetActor()));
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> Get(Guid projectId, Guid reportId, HttpContext ctx, GetReportHandler handler)
    {
        var result = await handler.HandleAsync(new GetReportRequest(projectId, reportId, ctx.GetActor()));
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

    private static async Task<IResult> ContextCost(Guid taskId, HttpContext ctx, ContextCostReportHandler handler)
    {
        var result = await handler.HandleAsync(new GetContextCostReportRequest(taskId, null, ctx.GetActor()));
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

}
