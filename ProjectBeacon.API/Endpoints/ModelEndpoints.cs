namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;

using Application.Agents;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API.Auth;

/// <summary>User-owned model registry and llama-swap proxy commands.</summary>
public static class ModelEndpoints
{
    /// <summary>
    /// The personal model registry and global proxy administration are Human-only.
    /// Project agent capabilities never grant model configuration or proxy control.
    /// </summary>
    public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", GetModelRegistry).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/models", UpsertModelBackend).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/models/{id:guid}", DeleteModelBackend).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/models/bind", BindRole).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/models/bind/{role}", UnbindRole).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/models/proxy/status", GetProxyStatus).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/models/proxy/reload", ReloadProxy).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/models/proxy/unload", UnloadProxy).RequireAuthorization().DisableAntiforgery().RequireHuman();
        return app;
    }

    private static async Task<IResult> GetModelRegistry(HttpContext ctx, GetModelRegistryHandler handler, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        if (actor.UserId is null)
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new GetModelRegistryCommand(actor.UserId!.Value), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> UpsertModelBackend(HttpContext ctx, [FromBody] UpsertLocalModelBackendRequest request, UpsertLocalModelBackendHandler handler, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        if (actor.UserId is null)
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new UpsertLocalModelBackendCommand(request with { UserId = actor.UserId!.Value }), ct);
        if (!result.Success)
            return result.FromResult();

        return request.Id is null
            ? Results.Created($"/v1/models/{result.Value!.Id}", result.Value)
            : Results.Ok(result.Value);
    }

    private static async Task<IResult> DeleteModelBackend(HttpContext ctx, Guid id, DeleteLocalModelBackendHandler handler, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        if (actor.UserId is null)
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new DeleteLocalModelBackendCommand(new DeleteLocalModelBackendRequest(id, actor.UserId!.Value)), ct);
        if (!result.Success)
            return result.Error!.StartsWith("Model backend not found")
                ? result.FromResult(404)
                : result.FromResult();

        return Results.NoContent();
    }
    /// <summary>
    /// Body for binding a pipeline role to a model backend.
    /// </summary>
    public record BindRoleBody(PipelineRole Role, Guid ModelBackendId);

    private static async Task<IResult> BindRole(HttpContext ctx, [FromBody] BindRoleBody body, SetRoleBindingHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new SetRoleBindingCommand(
            new SetRoleBindingRequest(body.Role, body.ModelBackendId),
            ctx.GetActor(),
            RequestedProjectId(ctx)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> UnbindRole(HttpContext ctx, string role, RemoveRoleBindingHandler handler, CancellationToken ct)
    {
        if (!Enum.TryParse<PipelineRole>(role, ignoreCase: true, out var parsed))
            return ProblemResults.Bad($"Unknown role '{role}'.");

        var result = await handler.HandleAsync(new RemoveRoleBindingCommand(
            new RemoveRoleBindingRequest(parsed), ctx.GetActor(), RequestedProjectId(ctx)), ct);
        return result.Success
            ? Results.NoContent()
            : result.FromResult(404);
    }

    private static Guid? RequestedProjectId(HttpContext ctx)
    {
        var value = ctx.Request.Headers["X-Project-Id"].ToString();
        return Guid.TryParse(value, out var projectId) && projectId != Guid.Empty
            ? projectId
            : null;
    }

    private static async Task<IResult> GetProxyStatus(GetProxyStatusHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetProxyStatusCommand(), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> ReloadProxy(ReloadProxyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ReloadProxyCommand(), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(503);
    }

    private static async Task<IResult> UnloadProxy(UnloadProxyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new UnloadProxyCommand(), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(503);
    }

}
