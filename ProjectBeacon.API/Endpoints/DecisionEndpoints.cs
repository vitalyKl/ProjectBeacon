namespace ProjectBeacon.API.Endpoints;

using Application.Decisions;
using Microsoft.AspNetCore.Mvc;

public static class DecisionEndpoints
{
    public static IEndpointRouteBuilder MapDecisionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects/{projectId:guid}/decisions", Create).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}/decisions", List).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/projects/{projectId:guid}/decisions/{decisionId:guid}/accept", Accept).RequireAuthorization().DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> Create(Guid projectId, [FromBody] CreateDecisionBody body, CreateDecisionHandler handler)
    {
        var result = await handler.HandleAsync(new CreateDecisionRequest(
            projectId, body.Title, body.Context ?? "", body.Body, body.Consequences));
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/decisions", result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> List(Guid projectId, ListDecisionsHandler handler)
    {
        var result = await handler.HandleAsync(projectId);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> Accept(Guid projectId, Guid decisionId, AcceptDecisionHandler handler)
    {
        var result = await handler.HandleAsync(decisionId);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    public record CreateDecisionBody(string Title, string? Context, string Body, string? Consequences);
}
