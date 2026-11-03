namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

using Application.Projects;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Label HTTP endpoints.
/// </summary>
public static class LabelEndpoints
{
    /// <summary>
    /// Maps label reads for human or TaskRead token actors, and label-path mutations for human actors.
    /// </summary>
    public static IEndpointRouteBuilder MapLabelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/projects/{projectId:guid}/labels", ListLabels).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapGet("/v1/projects/{projectId:guid}/labels/match", MatchLabel).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapPost("/v1/projects/{projectId:guid}/labels/{labelId:guid}/paths", AddPath).RequireAuthorization().DisableAntiforgery().RequireHuman();
        return app;
    }

    private static async Task<IResult> ListLabels(Guid projectId, ListLabelsHandler handler)
    {
        var result = await handler.HandleAsync(new ListLabelsRequest(projectId));
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> MatchLabel(Guid projectId, [FromQuery] string path, MatchLabelHandler handler)
    {
        var result = await handler.HandleAsync(new MatchLabelRequest(projectId, path));
        if (!result.Success)
            return result.FromResult();
        return result.Value is null ? Results.NoContent() : Results.Ok(result.Value);
    }

    private static async Task<IResult> AddPath(Guid projectId, Guid labelId, [FromBody] AddLabelPathBody body, AddLabelPathHandler handler)
    {
        var result = await handler.HandleAsync(new AddLabelPathRequest(projectId, labelId, body.Path));
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

    /// <summary>JSON body for <c>POST /v1/projects/{projectId:guid}/labels/{labelId:guid}/paths</c>.</summary>
    public record AddLabelPathBody(string Path);
}
