namespace ProjectBeacon.API.Endpoints;

using Application.Context;
using Application.Common;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;

public static class ContextEndpoints
{
    public static IEndpointRouteBuilder MapContextEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/projects/{projectId}/context/nodes", ListNodes).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/projects/{projectId}/context/nodes", UpsertNode).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId}/context/nodes/{nodeId}", GetNode).RequireAuthorization().DisableAntiforgery();
        app.MapDelete("/v1/projects/{projectId}/context/nodes/{nodeId}", DeleteNode).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/projects/{projectId}/context/import", ImportFiles).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId}/context/export/agents-md", ExportAgentsMd).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/projects/{projectId}/context/compile", CompileBrief).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}/constraints", ListConstraints).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/projects/{projectId:guid}/constraints", CreateConstraint).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/projects/{projectId:guid}/constraints/{constraintId:guid}/activate", ActivateConstraint).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/projects/{projectId:guid}/constraints/{constraintId:guid}/reject", RejectConstraint).RequireAuthorization().DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> ImportFiles(
        Guid projectId,
        [FromBody] IList<ImportFileRequest> files,
        ImportFilesHandler handler)
    {
        var command = new ImportFilesCommand(projectId, files);
        var result = await handler.HandleAsync(command);

        return result.Success
            ? Results.Ok(new { created = result.Value })
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> ExportAgentsMd(
        Guid projectId,
        [FromQuery] Guid? repoId,
        [FromQuery] string? path,
        ExportAgentsMdHandler handler)
    {
        var command = new ExportAgentsMdCommand(new ExportAgentsMdRequest(projectId, repoId, path));
        var result = await handler.HandleAsync(command);

        return result.Success
            ? Results.Text(result.Value, "text/markdown")
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> ListNodes(
        Guid projectId,
        ListContextNodesHandler handler)
    {
        var command = new ListContextNodesCommand(new ListContextNodesRequest(projectId));
        var result = await handler.HandleAsync(command);

        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> UpsertNode(
        Guid projectId,
        [FromBody] UpsertNodeRequest request,
        UpsertContextNodeHandler handler)
    {
        var command = new UpsertContextNodeCommand(new UpsertContextNodeRequest(
            projectId,
            request.Title,
            request.BodyMarkdown,
            request.SectionId,
            request.ScopeType,
            request.Key,
            request.RepoId,
            request.TaskId,
            request.Path,
            request.Source,
            request.SourcePath));

        var result = await handler.HandleAsync(command);

        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> GetNode(
        Guid projectId,
        Guid nodeId,
        GetContextNodeHandler handler)
    {
        var command = new GetContextNodeCommand(new GetContextNodeRequest(projectId, nodeId));
        var result = await handler.HandleAsync(command);

        return result.Success
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> DeleteNode(
        Guid projectId,
        Guid nodeId,
        DeleteContextNodeHandler handler)
    {
        var command = new DeleteContextNodeCommand(new DeleteContextNodeRequest(projectId, nodeId));
        var result = await handler.HandleAsync(command);

        return result.Success
            ? Results.Ok(new { success = true })
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> CompileBrief(
        Guid projectId,
        [FromBody] CompileBriefRequest request,
        CompileBriefHandler handler)
    {
        var command = new CompileBriefCommand(new Application.Context.CompileBriefRequest(
            projectId,
            request.RepoId,
            request.Path,
            request.TaskId,
            request.BudgetTokens,
            request.IncludeHandoff,
            request.IncludeChangedScope,
            request.IncludeTreeCapsule));

        var result = await handler.HandleAsync(command);

        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> ListConstraints(Guid projectId, ListConstraintsHandler handler)
    {
        var result = await handler.HandleAsync(projectId);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> CreateConstraint(Guid projectId, [FromBody] CreateConstraintBody body, CreateConstraintHandler handler)
    {
        var result = await handler.HandleAsync(new CreateConstraintRequest(projectId, body.Body, body.Kind));
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/constraints", result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> ActivateConstraint(Guid projectId, Guid constraintId, ActivateConstraintHandler handler)
    {
        var result = await handler.HandleAsync(constraintId);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> RejectConstraint(Guid projectId, Guid constraintId, RejectConstraintHandler handler)
    {
        var result = await handler.HandleAsync(constraintId);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    public record CreateConstraintBody(string Body, ConstraintKind Kind);

    public record UpsertNodeRequest(
        string Title,
        string BodyMarkdown,
        string? SectionId,
        ContextScopeType ScopeType,
        string? Key,
        Guid? RepoId,
        Guid? TaskId,
        string? Path,
        ContextSource Source,
        string? SourcePath);

    public record CompileBriefRequest(
        Guid? RepoId,
        string? Path,
        Guid? TaskId,
        int? BudgetTokens,
        bool IncludeHandoff,
        bool IncludeChangedScope,
        bool IncludeTreeCapsule);
}
