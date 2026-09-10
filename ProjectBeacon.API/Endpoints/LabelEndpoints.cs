namespace ProjectBeacon.API.Endpoints;

using Application.Projects;
using Microsoft.AspNetCore.Mvc;

public static class LabelEndpoints
{
    public static IEndpointRouteBuilder MapLabelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/projects/{projectId:guid}/labels", ListLabels).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}/labels/match", MatchLabel).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/projects/{projectId:guid}/labels/{labelId:guid}/paths", AddPath).RequireAuthorization().DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> ListLabels(Guid projectId, ListLabelsHandler handler)
    {
        var result = await handler.HandleAsync(new ListLabelsRequest(projectId));
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> MatchLabel(Guid projectId, [FromQuery] string path, MatchLabelHandler handler)
    {
        var result = await handler.HandleAsync(new MatchLabelRequest(projectId, path));
        if (!result.Success)
            return Results.BadRequest(new { error = result.Error });
        return result.Value is null ? Results.NoContent() : Results.Ok(result.Value);
    }

    private static async Task<IResult> AddPath(Guid projectId, Guid labelId, [FromBody] AddLabelPathBody body, AddLabelPathHandler handler)
    {
        var result = await handler.HandleAsync(new AddLabelPathRequest(projectId, labelId, body.Path));
        return result.Success
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    public record AddLabelPathBody(string Path);
}
