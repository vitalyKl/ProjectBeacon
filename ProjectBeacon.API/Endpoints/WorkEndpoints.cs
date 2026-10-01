namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;

using System.Security.Claims;
using Application.Common;
using Application.Tasks;
using Microsoft.AspNetCore.Mvc;
using FinishWorkReview = ProjectBeacon.Application.Tasks.FinishWorkReview;

public static class WorkEndpoints
{
    public static IEndpointRouteBuilder MapWorkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/work/finish_work", FinishWork).RequireAuthorization().DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> FinishWork(
        [FromBody] FinishWorkRequest request,
        FinishWorkHandler handler,
        HttpContext ctx,
        CancellationToken ct)
    {
        var actorId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(actorId))
            return Results.Unauthorized();

        var command = new FinishWorkCommand(new Application.Tasks.FinishWorkRequest(
            request.TaskId,
            request.Result,
            request.Output,
            actorId,
            request.Review is null
                ? null
                : new FinishWorkReview(request.Review.ReviewerRun, request.Review.RegressionsFound, request.Review.RegressionsFixed),
            request.ReviewTranscriptRef,
            request.ReviewRunId));

        var result = await handler.HandleAsync(command, ct);

        return result.Success
            ? Results.Ok(new { success = true })
            : result.FromResult();
    }

    public record FinishWorkReviewDto(bool ReviewerRun, int RegressionsFound, int RegressionsFixed);

    public record FinishWorkRequest(
        string TaskId,
        string Result,
        string? Output,
        FinishWorkReviewDto? Review = null,
        string? ReviewTranscriptRef = null,
        Guid? ReviewRunId = null);
}
