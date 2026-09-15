namespace ProjectBeacon.API.Endpoints;

using Application.Agents;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API.Auth;

public static class ModelEndpoints
{
    public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", GetModelRegistry).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskRead);
        app.MapPost("/v1/models", UpsertModelBackend).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapDelete("/v1/models/{id:guid}", DeleteModelBackend).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/models/bind", BindRole).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapDelete("/v1/models/bind/{role}", UnbindRole).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapGet("/v1/models/proxy/status", GetProxyStatus).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskRead);
        app.MapPost("/v1/models/proxy/reload", ReloadProxy).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/models/proxy/unload", UnloadProxy).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        return app;
    }

    private static async Task<IResult> GetModelRegistry(GetModelRegistryHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetModelRegistryCommand(), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> UpsertModelBackend([FromBody] UpsertLocalModelBackendRequest request, UpsertLocalModelBackendHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new UpsertLocalModelBackendCommand(request), ct);
        if (!result.Success)
            return Results.BadRequest(new { error = result.Error });

        return request.Id is null
            ? Results.Created($"/v1/models/{result.Value.Id}", result.Value)
            : Results.Ok(result.Value);
    }

    private static async Task<IResult> DeleteModelBackend(Guid id, DeleteLocalModelBackendHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new DeleteLocalModelBackendCommand(new DeleteLocalModelBackendRequest(id)), ct);
        if (!result.Success)
            return result.Error!.StartsWith("Model backend not found")
                ? Results.NotFound(new { error = result.Error })
                : Results.BadRequest(new { error = result.Error });

        return Results.NoContent();
    }

    public record BindRoleBody(PipelineRole Role, Guid ModelBackendId);

    private static async Task<IResult> BindRole([FromBody] BindRoleBody body, SetRoleBindingHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new SetRoleBindingCommand(new SetRoleBindingRequest(body.Role, body.ModelBackendId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> UnbindRole(string role, RemoveRoleBindingHandler handler, CancellationToken ct)
    {
        if (!Enum.TryParse<PipelineRole>(role, ignoreCase: true, out var parsed))
            return Results.BadRequest(new { error = $"Unknown role '{role}'." });

        var result = await handler.HandleAsync(new RemoveRoleBindingCommand(new RemoveRoleBindingRequest(parsed)), ct);
        return result.Success
            ? Results.NoContent()
            : Results.NotFound(new { error = result.Error });
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
            : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> UnloadProxy(UnloadProxyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new UnloadProxyCommand(), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
