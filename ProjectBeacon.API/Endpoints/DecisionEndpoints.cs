namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

using Application.Decisions;
using Application.Authorization;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Decision HTTP endpoints.
/// </summary>
public static class DecisionEndpoints
{
    /// <summary>
    /// Create and lifecycle decisions are Human-only. Proposals allow Humans or task-scoped Actor tokens; list/get allow scoped <c>ContextRead</c> tokens.
    /// </summary>
    public static IEndpointRouteBuilder MapDecisionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects/{projectId:guid}/decisions", Create).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/decisions/propose", Propose)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapGet("/v1/projects/{projectId:guid}/decisions", List).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapGet("/v1/projects/{projectId:guid}/decisions/{decisionId:guid}", Get)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapPost("/v1/projects/{projectId:guid}/decisions/{decisionId:guid}/accept", Accept).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/decisions/{decisionId:guid}/deprecate", Deprecate).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/decisions/{decisionId:guid}/supersede", Supersede).RequireAuthorization().DisableAntiforgery().RequireHuman();
        return app;
    }

    private static async Task<IResult> Propose(
        Guid projectId,
        [FromBody] CreateDecisionBody body,
        HttpContext context,
        CreateDecisionHandler handler,
        CancellationToken ct)
    {
        if (!IsHumanOrActorKnowledgeWriter(context))
            return Results.Forbid();
        var result = await handler.HandleProposalAsync(new CreateDecisionRequest(
            projectId, body.Title, body.Context ?? string.Empty, body.Body, body.Consequences), context.GetActor(), ct);
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/decisions/propose", result.Value)
            : result.FromResult();
    }

    private static bool IsHumanOrActorKnowledgeWriter(HttpContext context)
    {
        var apiTokenIdentity = context.User.Identities.Any(identity =>
            identity.AuthenticationType == ActorContextFactory.ApiTokenAuthType);
        return !apiTokenIdentity
            || string.Equals(context.User.FindFirst("pipeline_role")?.Value,
                PipelineRole.Actor.ToString(), StringComparison.Ordinal);
    }

    private static async Task<IResult> Create(Guid projectId, [FromBody] CreateDecisionBody body, HttpContext context, CreateDecisionHandler handler)
    {
        var result = await handler.HandleAsync(new CreateDecisionRequest(
            projectId, body.Title, body.Context ?? "", body.Body, body.Consequences), context.GetActor());
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/decisions", result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> List(Guid projectId, HttpContext context, ListDecisionsHandler handler)
    {
        var result = await handler.HandleAsync(projectId, context.GetActor());
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> Get(Guid projectId, Guid decisionId, HttpContext context, GetDecisionHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(projectId, decisionId, context.GetActor(), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> Accept(Guid projectId, Guid decisionId, HttpContext context, AcceptDecisionHandler handler)
    {
        var result = await handler.HandleAsync(decisionId, context.GetActor());
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

    private static async Task<IResult> Deprecate(Guid projectId, Guid decisionId, HttpContext context, DeprecateDecisionHandler handler)
    {
        var result = await handler.HandleAsync(decisionId, context.GetActor());
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

    private static async Task<IResult> Supersede(
        Guid projectId,
        Guid decisionId,
        [FromBody] SupersedeBody body,
        HttpContext context,
        SupersedeDecisionHandler handler)
    {
        var result = await handler.HandleAsync(decisionId, body.ReplacementId, context.GetActor());
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    /// <summary>JSON body for <c>POST /v1/projects/{projectId:guid}/decisions</c>.</summary>
    public record CreateDecisionBody(string Title, string? Context, string Body, string? Consequences);

    /// <summary>JSON body for <c>POST /v1/projects/{projectId:guid}/decisions/{decisionId:guid}/supersede</c>.</summary>
    public record SupersedeBody(Guid ReplacementId);
}
