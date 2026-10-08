namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

using Application.Common;
using Application.Tasks;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using FinishWorkReview = ProjectBeacon.Application.Tasks.FinishWorkReview;

/// <summary>
/// Work-completion HTTP endpoints.
/// </summary>
public static class WorkEndpoints
{
    /// <summary>
    /// Maps <c>POST /v1/work/finish_work</c> with <c>RequireHumanOrApiToken(ApiTokenCapability.TaskWrite)</c>.
    /// The actor passed to the handler is <c>HttpContext.GetActor().UserId</c>, not a body field. <c>done</c> still depends on the handler requiring <c>reviewRunId</c>.
    /// </summary>
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

    /// <summary>Optional review counts on <c>POST /v1/work/finish_work</c>. Not proof that the work is done.</summary>
    public record FinishWorkReviewDto(bool ReviewerRun, int RegressionsFound, int RegressionsFixed);

    /// <summary>
    /// JSON body for <c>POST /v1/work/finish_work</c>. Has no caller id; the handler actor is <c>HttpContext.GetActor().UserId</c>.
    /// </summary>
    public record FinishWorkRequest(
        string TaskId,
        string Result,
        string? Output,
        FinishWorkReviewDto? Review = null,
        string? ReviewTranscriptRef = null,
        Guid? ReviewRunId = null);
}
