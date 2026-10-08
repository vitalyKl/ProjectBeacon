namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

using Application.Decisions;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Decision HTTP endpoints.
/// </summary>
public static class DecisionEndpoints
{
    /// <summary>
    /// Maps <c>POST /v1/projects/{projectId:guid}/decisions</c>, <c>GET /v1/projects/{projectId:guid}/decisions</c>, <c>POST /v1/projects/{projectId:guid}/decisions/{decisionId:guid}/accept</c>, <c>POST /v1/projects/{projectId:guid}/decisions/{decisionId:guid}/deprecate</c>, and <c>POST /v1/projects/{projectId:guid}/decisions/{decisionId:guid}/supersede</c> with <c>RequireHuman</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapDecisionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects/{projectId:guid}/decisions", Create).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/decisions", List).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/decisions/{decisionId:guid}/accept", Accept).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/decisions/{decisionId:guid}/deprecate", Deprecate).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/decisions/{decisionId:guid}/supersede", Supersede).RequireAuthorization().DisableAntiforgery().RequireHuman();
        return app;
    }

    private static async Task<IResult> Create(Guid projectId, [FromBody] CreateDecisionBody body, CreateDecisionHandler handler)
    {
        var result = await handler.HandleAsync(new CreateDecisionRequest(
            projectId, body.Title, body.Context ?? "", body.Body, body.Consequences));
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/decisions", result.Value)
            : result.FromResult();
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
            : result.FromResult(404);
    }

    private static async Task<IResult> Deprecate(Guid projectId, Guid decisionId, DeprecateDecisionHandler handler)
    {
        var result = await handler.HandleAsync(decisionId);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

    private static async Task<IResult> Supersede(
        Guid projectId,
        Guid decisionId,
        [FromBody] SupersedeBody body,
        SupersedeDecisionHandler handler)
    {
        var result = await handler.HandleAsync(decisionId, body.ReplacementId);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    /// <summary>JSON body for <c>POST /v1/projects/{projectId:guid}/decisions</c>.</summary>
    public record CreateDecisionBody(string Title, string? Context, string Body, string? Consequences);

    /// <summary>JSON body for <c>POST /v1/projects/{projectId:guid}/decisions/{decisionId:guid}/supersede</c>.</summary>
    public record SupersedeBody(Guid ReplacementId);
}
