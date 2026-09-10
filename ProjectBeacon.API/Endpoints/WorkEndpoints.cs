namespace ProjectBeacon.API.Endpoints;

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
        CancellationToken ct)
    {
        var command = new FinishWorkCommand(new Application.Tasks.FinishWorkRequest(
            request.TaskId,
            request.Result,
            request.Output,
            request.ActorId,
            request.Review is null
                ? null
                : new FinishWorkReview(request.Review.ReviewerRun, request.Review.RegressionsFound, request.Review.RegressionsFixed)));

        var result = await handler.HandleAsync(command, ct);

        return result.Success
            ? Results.Ok(new { success = true })
            : Results.BadRequest(new { error = result.Error });
    }

    public record FinishWorkReviewDto(bool ReviewerRun, int RegressionsFound, int RegressionsFixed);

    public record FinishWorkRequest(
        string TaskId,
        string Result,
        string? Output,
        string ActorId,
        FinishWorkReviewDto? Review = null);
}
