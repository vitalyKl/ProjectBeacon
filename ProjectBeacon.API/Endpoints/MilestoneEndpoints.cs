namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

using Application.Common;
using Application.Milestones;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

public static class MilestoneEndpoints
{
    public static IEndpointRouteBuilder MapMilestoneEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects/{projectId:guid}/milestones", CreateMilestone).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPut("/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}", UpdateMilestone).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPut("/v1/milestones/{milestoneId:guid}", UpdateMilestone).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}", DeleteMilestone).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/milestones/{milestoneId:guid}", DeleteMilestone).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/milestones", ListMilestones).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}", GetMilestone).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/milestones/{milestoneId:guid}", GetMilestone).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}/close", CloseMilestone).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/milestones/{milestoneId:guid}/reopen", ReopenMilestone).RequireAuthorization().DisableAntiforgery().RequireHuman();

        return app;
    }

    private static async Task<IResult> CreateMilestone(Guid projectId, [FromBody] CreateMilestoneRequest request, CreateMilestoneHandler handler)
    {
        if (projectId != request.ProjectId)
            return ProblemResults.Bad("ProjectId mismatch.");

        var result = await handler.HandleAsync(new CreateMilestoneCommand(request));

        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/milestones/{result.Value!.Id}", MapMilestoneResponse(result.Value!))
            : result.FromResult();
    }

    private static async Task<IResult> UpdateMilestone(Guid milestoneId, [FromBody] UpdateMilestoneRequest request, UpdateMilestoneHandler handler)
    {
        if (milestoneId != request.MilestoneId)
            return ProblemResults.Bad("MilestoneId mismatch.");

        var result = await handler.HandleAsync(new UpdateMilestoneCommand(request));

        return result.Success
            ? Results.Ok(MapMilestoneResponse(result.Value!))
            : result.FromResult(404);
    }

    private static async Task<IResult> DeleteMilestone(Guid milestoneId, DeleteMilestoneHandler handler)
    {
        var result = await handler.HandleAsync(new DeleteMilestoneCommand(new DeleteMilestoneRequest(milestoneId)));

        return result.Success
            ? Results.NoContent()
            : result.FromResult(404);
    }

    private static async Task<IResult> ListMilestones(Guid projectId, CancellationToken ct, ListProjectMilestonesHandler handler)
    {
        var result = await handler.HandleAsync(new ListProjectMilestonesCommand(new ListProjectMilestonesRequest(projectId)), ct);

        return result.Success
            ? Results.Ok(result.Value!.Select(MapMilestoneResponse))
            : result.FromResult();
    }

    private static async Task<IResult> GetMilestone(Guid milestoneId, GetMilestoneHandler handler)
    {
        var result = await handler.HandleAsync(new GetMilestoneCommand(new GetMilestoneRequest(milestoneId)));

        return result.Success
            ? Results.Ok(MapMilestoneResponse(result.Value!))
            : result.FromResult(404);
    }

    private static async Task<IResult> CloseMilestone(Guid projectId, Guid milestoneId, CloseMilestoneHandler handler)
    {
        var result = await handler.HandleAsync(new CloseMilestoneCommand(new CloseMilestoneRequest(milestoneId)));
        return result.Success
            ? Results.Ok(MapMilestoneResponse(result.Value!))
            : result.FromResult(404);
    }

    private static async Task<IResult> ReopenMilestone(Guid projectId, Guid milestoneId, ReopenMilestoneHandler handler)
    {
        var result = await handler.HandleAsync(new ReopenMilestoneCommand(new ReopenMilestoneRequest(milestoneId)));
        return result.Success
            ? Results.Ok(MapMilestoneResponse(result.Value!))
            : result.FromResult(404);
    }

    private static MilestoneDto MapMilestoneResponse(MilestoneDto dto) =>
        new(dto.Id, dto.Name, dto.Description, dto.ProjectId, dto.Order, dto.CreatedAt, dto.UpdatedAt, dto.ClosedAt);
}
