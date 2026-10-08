namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;

using Application.Context;
using Application.Common;
using Domain.Enums;
using ProjectBeacon.API.Auth;
using Microsoft.AspNetCore.Mvc;

/// <summary>Context sections, brief compile, and constraints. Tokens may read context. Writes stay human.</summary>
public static class ContextEndpoints
{
    /// <summary>
    /// Reads (<c>nodes</c>, export, compile, list constraints) allow <see cref="ApiTokenCapability.ContextRead"/>.
    /// Upsert, delete, import, and constraint create, activate, and reject are <c>RequireHuman</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapContextEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/projects/{projectId}/context/nodes", ListNodes).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapPost("/v1/projects/{projectId}/context/nodes", UpsertNode).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId}/context/nodes/{nodeId}", GetNode).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapDelete("/v1/projects/{projectId}/context/nodes/{nodeId}", DeleteNode).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId}/context/import", ImportFiles).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId}/context/export/agents-md", ExportAgentsMd).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapPost("/v1/projects/{projectId}/context/compile", CompileBrief).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapGet("/v1/projects/{projectId:guid}/constraints", ListConstraints).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapPost("/v1/projects/{projectId:guid}/constraints", CreateConstraint).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/constraints/{constraintId:guid}/activate", ActivateConstraint).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/constraints/{constraintId:guid}/reject", RejectConstraint).RequireAuthorization().DisableAntiforgery().RequireHuman();

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
            : result.FromResult();
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
            : result.FromResult();
    }

    private static async Task<IResult> ListNodes(
        Guid projectId,
        ListContextNodesHandler handler)
    {
        var command = new ListContextNodesCommand(new ListContextNodesRequest(projectId));
        var result = await handler.HandleAsync(command);

        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
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
            : result.FromResult();
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
            : result.FromResult(404);
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
            : result.FromResult(404);
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
            request.IncludeTreeCapsule,
            request.TreeCapsule,
            request.ChangedScope));

        var result = await handler.HandleAsync(command);

        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
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
            : result.FromResult();
    }

    private static async Task<IResult> ActivateConstraint(Guid projectId, Guid constraintId, ActivateConstraintHandler handler)
    {
        var result = await handler.HandleAsync(constraintId);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

    private static async Task<IResult> RejectConstraint(Guid projectId, Guid constraintId, RejectConstraintHandler handler)
    {
        var result = await handler.HandleAsync(constraintId);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }
    /// <summary>
    /// Body for proposing a constraint: text and kind.
    /// </summary>
    public record CreateConstraintBody(string Body, ConstraintKind Kind);
    /// <summary>
    /// Body for creating or updating a context section. Title, body, and scope are required by the handler.
    /// </summary>
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
    /// <summary>
    /// Body for compiling a brief: optional repo, path, task, token budget, and which capsules to include.
    /// </summary>
    public record CompileBriefRequest(
        Guid? RepoId,
        string? Path,
        Guid? TaskId,
        int? BudgetTokens,
        bool IncludeHandoff,
        bool IncludeChangedScope,
        bool IncludeTreeCapsule,
        string? TreeCapsule = null,
        string? ChangedScope = null);
}
