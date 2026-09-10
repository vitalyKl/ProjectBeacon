namespace ProjectBeacon.API.Endpoints;

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
        app.MapPost("/v1/projects/{projectId:guid}/milestones", CreateMilestone).RequireAuthorization().DisableAntiforgery();
        app.MapPut("/v1/milestones/{milestoneId:guid}", UpdateMilestone).RequireAuthorization().DisableAntiforgery();
        app.MapDelete("/v1/milestones/{milestoneId:guid}", DeleteMilestone).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}/milestones", ListMilestones).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/milestones/{milestoneId:guid}", GetMilestone).RequireAuthorization().DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> CreateMilestone(Guid projectId, [FromBody] CreateMilestoneRequest request, CreateMilestoneHandler handler)
    {
        if (projectId != request.ProjectId)
            return Results.BadRequest("ProjectId mismatch.");

        var result = await handler.HandleAsync(new CreateMilestoneCommand(request));

        return result.Success
            ? Results.Created($"/v1/milestones/{result.Value.Id}", MapMilestoneResponse(result.Value))
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> UpdateMilestone(Guid milestoneId, [FromBody] UpdateMilestoneRequest request, UpdateMilestoneHandler handler)
    {
        if (milestoneId != request.MilestoneId)
            return Results.BadRequest("MilestoneId mismatch.");

        var result = await handler.HandleAsync(new UpdateMilestoneCommand(request));

        return result.Success
            ? Results.Ok(MapMilestoneResponse(result.Value))
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> DeleteMilestone(Guid milestoneId, DeleteMilestoneHandler handler)
    {
        var result = await handler.HandleAsync(new DeleteMilestoneCommand(new DeleteMilestoneRequest(milestoneId)));

        return result.Success
            ? Results.NoContent()
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> ListMilestones(Guid projectId, CancellationToken ct, ListProjectMilestonesHandler handler)
    {
        var result = await handler.HandleAsync(new ListProjectMilestonesCommand(new ListProjectMilestonesRequest(projectId)), ct);

        return result.Success
            ? Results.Ok(result.Value.Select(MapMilestoneResponse))
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> GetMilestone(Guid milestoneId, GetMilestoneHandler handler)
    {
        var result = await handler.HandleAsync(new GetMilestoneCommand(new GetMilestoneRequest(milestoneId)));

        return result.Success
            ? Results.Ok(MapMilestoneResponse(result.Value))
            : Results.NotFound(new { error = result.Error });
    }

    private static MilestoneDto MapMilestoneResponse(MilestoneDto dto) =>
        new(dto.Id, dto.Name, dto.Description, dto.ProjectId, dto.Order, dto.CreatedAt, dto.UpdatedAt, dto.ClosedAt);
}
