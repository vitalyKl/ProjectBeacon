namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;

using Application.Context;
using Application.Authorization;
using Application.Common;
using Domain.Enums;
using ProjectBeacon.API.Auth;
using Microsoft.AspNetCore.Mvc;

/// <summary>Context reads are token-scoped; normal edits remain human-only. The dedicated draft endpoint permits only the scoped knowledge-curator Actor.</summary>
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
        app.MapPost("/v1/projects/{projectId}/context/drafts", CreateDraft)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapGet("/v1/projects/{projectId}/context/nodes/{nodeId}", GetNode).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapDelete("/v1/projects/{projectId}/context/nodes/{nodeId}", DeleteNode).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId}/context/import", ImportFiles).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId}/context/export/agents-md", ExportAgentsMd).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapPost("/v1/projects/{projectId}/context/compile", CompileBrief).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapGet("/v1/projects/{projectId:guid}/constraints", ListConstraints).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapGet("/v1/projects/{projectId:guid}/constraints/{constraintId:guid}", GetConstraint)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapGet("/v1/projects/{projectId:guid}/content-versions", ListContentVersions)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapPost("/v1/projects/{projectId:guid}/content-versions/{versionId:guid}/restore", RestoreContentVersion)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/constraints", CreateConstraint).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/constraints/propose", ProposeConstraint)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapPost("/v1/projects/{projectId:guid}/constraints/{constraintId:guid}/activate", ActivateConstraint).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/constraints/{constraintId:guid}/reject", RejectConstraint).RequireAuthorization().DisableAntiforgery().RequireHuman();

        return app;
    }

    private static async Task<IResult> CreateDraft(
        Guid projectId,
        [FromBody] UpsertNodeRequest request,
        HttpContext context,
        UpsertContextNodeHandler handler,
        CancellationToken ct)
    {
        if (!IsHumanOrActorKnowledgeWriter(context))
            return Results.Forbid();

        var actor = context.GetActor();
        var authorId = context.User.FindFirst("pipeline_subtask_id")?.Value
            ?? context.User.FindFirst("pipeline_session_id")?.Value
            ?? actor.UserId?.ToString("D");
        var command = new UpsertContextNodeCommand(new UpsertContextNodeRequest(
            projectId, request.Title, request.BodyMarkdown, request.SectionId,
            request.ScopeType, request.Key, request.RepoId, request.TaskId,
            request.Path, ContextSource.Native, request.SourcePath, request.ExpectedRevision), actor);
        var result = await handler.HandleDraftAsync(command, authorId, ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> ProposeConstraint(
        Guid projectId,
        [FromBody] CreateConstraintBody body,
        HttpContext context,
        CreateConstraintHandler handler,
        CancellationToken ct)
    {
        if (!IsHumanOrActorKnowledgeWriter(context))
            return Results.Forbid();
        var actor = context.GetActor();
        var agentId = context.User.FindFirst("pipeline_subtask_id")?.Value
            ?? context.User.FindFirst("pipeline_session_id")?.Value;
        var result = await handler.HandleProposalAsync(
            new CreateConstraintRequest(projectId, body.Body, body.Kind, actor, agentId), ct);
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/constraints/propose", result.Value)
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

    private static async Task<IResult> ImportFiles(
        Guid projectId,
        [FromBody] IList<ImportFileRequest> files,
        HttpContext context,
        ImportFilesHandler handler)
    {
        var command = new ImportFilesCommand(projectId, files, context.GetActor());
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
        HttpContext context,
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
            request.SourcePath,
            request.ExpectedRevision), context.GetActor());

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
        HttpContext context,
        DeleteContextNodeHandler handler)
    {
        var command = new DeleteContextNodeCommand(new DeleteContextNodeRequest(projectId, nodeId), context.GetActor());
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

    private static async Task<IResult> GetConstraint(
        Guid projectId, Guid constraintId, GetConstraintHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(projectId, constraintId, ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }


    private static async Task<IResult> ListContentVersions(
        Guid projectId,
        ContentTargetKind targetKind,
        Guid targetId,
        int? limit,
        HttpContext context,
        ListContentVersionsHandler handler,
        CancellationToken ct)
    {
        // Task history has its own TaskRead capability and endpoint. Do not let a token
        // holding only ContextRead enumerate task descriptions through this generic route.
        if (targetKind == ContentTargetKind.TaskDescription)
            return Results.Forbid();

        var result = await handler.HandleAsync(
            projectId, targetKind, targetId, context.GetActor(), limit ?? 50, ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> RestoreContentVersion(
        Guid projectId,
        Guid versionId,
        [FromBody] RestoreContentVersionRequest request,
        HttpContext context,
        RestoreContentVersionHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new RestoreContentVersionCommand(
            projectId, versionId, request.ExpectedRevision, context.GetActor()), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> CreateConstraint(
        Guid projectId,
        [FromBody] CreateConstraintBody body,
        HttpContext context,
        CreateConstraintHandler handler)
    {
        var result = await handler.HandleAsync(new CreateConstraintRequest(projectId, body.Body, body.Kind, context.GetActor()));
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/constraints", result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> ActivateConstraint(
        Guid projectId, Guid constraintId, HttpContext context, ActivateConstraintHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(constraintId, context.GetActor(), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

    private static async Task<IResult> RejectConstraint(
        Guid projectId, Guid constraintId, HttpContext context, RejectConstraintHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(constraintId, context.GetActor(), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }
    /// <summary>
    /// Body for proposing a constraint: text and kind.
    /// </summary>
    public record CreateConstraintBody(string Body, ConstraintKind Kind);

    /// <summary>Expected current revision when restoring a historical content snapshot.</summary>
    public record RestoreContentVersionRequest(int ExpectedRevision);
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
        string? SourcePath,
        int? ExpectedRevision = null);
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
