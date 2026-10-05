namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

using Application.Common;
using Application.Tasks;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using FinishWorkReview = ProjectBeacon.Application.Tasks.FinishWorkReview;

public static class WorkEndpoints
{
    public static IEndpointRouteBuilder MapWorkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/work/finish_work", FinishWork).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);

        return app;
    }

    private static async Task<IResult> FinishWork(
        [FromBody] FinishWorkRequest request,
        FinishWorkHandler handler,
        HttpContext ctx,
        CancellationToken ct)
    {
        var actor = ctx.GetActor();
        if (actor.UserId is null)
            return ProblemResults.Unauthorized();

        var command = new FinishWorkCommand(new Application.Tasks.FinishWorkRequest(
            request.TaskId,
            request.Result,
            request.Output,
            actor.UserId!.Value.ToString(),
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
