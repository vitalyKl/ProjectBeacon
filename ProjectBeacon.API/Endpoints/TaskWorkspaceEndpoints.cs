namespace ProjectBeacon.API.Endpoints;

using Application.Authorization;
using Application.Common;
using Application.Tasks;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

public static class TaskWorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapTaskWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/tasks/{taskId:guid}/workspace", Index)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapGet("/v1/tasks/{taskId:guid}/workspace/{**key}", Read)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapPost("/v1/tasks/{taskId:guid}/workspace", Write)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        return app;
    }

    private static async Task<IResult> Index(
        Guid taskId, [FromQuery] string? query, [FromQuery] string? kind, [FromQuery] DateTime? since,
        [FromQuery] int? limit, WorkspaceIndexHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new WorkspaceIndexQuery(
            new WorkspaceIndexRequest(taskId, query, kind, since, limit ?? 20)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> Read(
        Guid taskId, string key, [FromQuery] int? offset, [FromQuery] int? limit,
        WorkspaceReadHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new WorkspaceReadQuery(
            new WorkspaceReadRequest(taskId, key, offset ?? 0, limit ?? 8000)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> Write(
        Guid taskId, [FromBody] WorkspaceWriteBody body, HttpContext context,
        WorkspaceWriteHandler handler, CancellationToken ct)
    {
        if (body.TaskId is { } bodyTaskId && bodyTaskId != taskId)
            return ProblemResults.Bad("TaskId mismatch.");
        var request = new WorkspaceWriteRequest(taskId, body.Key, body.Title, body.Abstract,
            body.Content, body.Mode ?? "append", body.ExpectedRevision);
        var result = await handler.HandleAsync(new WorkspaceWriteCommand(
            request, context.GetActor(), context.User.FindFirst("pipeline_role")?.Value,
            ParseClaimGuid(context, "pipeline_subtask_id"), ParseClaimGuid(context, "pipeline_session_id")), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static Guid? ParseClaimGuid(HttpContext context, string claimType) =>
        Guid.TryParse(context.User.FindFirst(claimType)?.Value, out var value) ? value : null;

    public sealed record WorkspaceWriteBody(
        Guid? TaskId, string Key, string? Title, string? Abstract, string Content,
        string? Mode, int? ExpectedRevision);
}
