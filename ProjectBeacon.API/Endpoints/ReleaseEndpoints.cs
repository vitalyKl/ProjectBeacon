namespace ProjectBeacon.API.Endpoints;

using Application.Authorization;
using Application.Common;
using Application.Releases;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

/// <summary>HTTP endpoints for optional project Release Management.</summary>
public static class ReleaseEndpoints
{
    public static IEndpointRouteBuilder MapReleaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/projects/{projectId:guid}/releases", ListReleases)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapGet("/v1/projects/{projectId:guid}/releases/{releaseId:guid}", GetRelease)
            .RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapPost("/v1/projects/{projectId:guid}/releases", CreateRelease)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPut("/v1/projects/{projectId:guid}/releases/{releaseId:guid}", UpdateRelease)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPatch("/v1/projects/{projectId:guid}/releases/{releaseId:guid}/status", SetReleaseStatus)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/projects/{projectId:guid}/releases/{releaseId:guid}", DeleteRelease)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPut("/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}/release", SetMilestoneRelease)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        return app;
    }

    private static async Task<IResult> ListReleases(
        Guid projectId, HttpContext context, ListProjectReleasesHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new ListProjectReleasesCommand(new ListProjectReleasesRequest(projectId), context.GetActor()), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> GetRelease(
        Guid projectId, Guid releaseId, HttpContext context, GetReleaseHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new GetReleaseCommand(new GetReleaseRequest(releaseId, projectId), context.GetActor()), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> CreateRelease(
        Guid projectId, [FromBody] CreateReleaseRequest request,
        HttpContext context, CreateReleaseHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new CreateReleaseCommand(projectId, request, context.GetActor()), ct);
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/releases/{result.Value!.Id}", result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> UpdateRelease(
        Guid projectId, Guid releaseId, [FromBody] UpdateReleaseBody body,
        HttpContext context, UpdateReleaseHandler handler, CancellationToken ct)
    {
        if (body.ReleaseId != releaseId || body.ProjectId != projectId)
            return ProblemResults.Bad("ReleaseId or ProjectId mismatch.");
        var result = await handler.HandleAsync(new UpdateReleaseCommand(
            new UpdateReleaseRequest(releaseId, projectId, body.Name, body.Description,
                body.TargetVersion, body.TargetBranch, body.Order, body.ExpectedRevision),
            context.GetActor()), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> SetReleaseStatus(
        Guid projectId, Guid releaseId, [FromBody] SetReleaseStatusBody body,
        HttpContext context, SetReleaseStatusHandler handler, CancellationToken ct)
    {
        if (body.ProjectId != projectId)
            return ProblemResults.Bad("ProjectId mismatch.");
        var result = await handler.HandleAsync(new SetReleaseStatusCommand(
            new SetReleaseStatusRequest(releaseId, projectId, body.Status, body.ExpectedRevision),
            context.GetActor()), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> DeleteRelease(
        Guid projectId, Guid releaseId, HttpContext context, DeleteReleaseHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new DeleteReleaseCommand(
            new DeleteReleaseRequest(releaseId, projectId), context.GetActor()), ct);
        return result.Success ? Results.NoContent() : result.FromResult();
    }

    private static async Task<IResult> SetMilestoneRelease(
        Guid projectId, Guid milestoneId, [FromBody] SetMilestoneReleaseBody body,
        HttpContext context, SetMilestoneReleaseHandler handler, CancellationToken ct)
    {
        if (body.ReleaseId == Guid.Empty)
            return ProblemResults.Bad("ReleaseId cannot be an empty GUID; send null to detach.");
        var result = await handler.HandleAsync(new SetMilestoneReleaseCommand(
            new SetMilestoneReleaseRequest(milestoneId, projectId, body.ReleaseId, body.ExpectedRevision),
            context.GetActor()), ct);
        return result.Success ? Results.NoContent() : result.FromResult();
    }

    public sealed record UpdateReleaseBody(
        Guid ReleaseId, Guid ProjectId, string? Name, string? Description,
        string? TargetVersion, string? TargetBranch, int? Order, int ExpectedRevision);

    public sealed record SetReleaseStatusBody(
        Guid ProjectId, Domain.Enums.ReleaseStatus Status, int ExpectedRevision);

    public sealed record SetMilestoneReleaseBody(Guid? ReleaseId, int ExpectedRevision);
}
