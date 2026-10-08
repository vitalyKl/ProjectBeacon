namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;

using Application.Common;
using Application.Tasks;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API.Auth;

/// <summary>
/// Task pipeline HTTP endpoints.
/// </summary>
public static class PipelineEndpoints
{
    /// <summary>
    /// Maps <c>GET /v1/tasks/{taskId:guid}/pipeline</c> with <c>RequireHumanOrApiToken(ApiTokenCapability.TaskRead)</c>.
    /// Maps <c>POST /v1/tasks/{taskId:guid}/pipeline/start</c>, <c>POST /v1/tasks/{taskId:guid}/subtasks</c>, <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/session</c>, <c>POST /v1/sessions/{sessionId:guid}/launch</c>, <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/result</c>, <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/fail</c>, <c>POST /v1/tasks/{taskId:guid}/pipeline/review/start</c>, <c>POST /v1/tasks/{taskId:guid}/pipeline/verdict</c>, <c>POST /v1/tasks/{taskId:guid}/pipeline/review/check</c>, and <c>POST /v1/tasks/{taskId:guid}/pipeline/approve</c> with <c>RequireHumanOrApiToken(ApiTokenCapability.TaskWrite)</c>.
    /// Maps <c>POST /v1/tasks/{taskId:guid}/pipeline/force-close</c> with <c>RequireHumanOrApiToken(ApiTokenCapability.Admin)</c>. The body is task id plus an optional reason.
    /// </summary>
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/tasks/{taskId:guid}/pipeline", GetPipeline).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/start", StartPipeline).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks", CreateSubtask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/session", StartActorSession).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/sessions/{sessionId:guid}/launch", LaunchSession).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/result", ReportSubtaskResult).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/fail", FailSubtask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/review/start", StartReview).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/verdict", RecordReviewVerdict).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/review/check", RecordReviewCheck).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/approve", ApprovePipeline).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/force-close", ForceClosePipeline).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.Admin);

        return app;
    }

    private static async Task<IResult> GetPipeline(Guid taskId, GetPipelineHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetPipelineCommand(new GetPipelineRequest(taskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

    private static async Task<IResult> StartPipeline(Guid taskId, [FromBody] PipelineStartBody body, StartPipelineHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new StartPipelineCommand(new StartPipelineRequest(body.TaskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> CreateSubtask(Guid taskId, [FromBody] CreateSubtaskBody body, CreateSubtaskHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            body.TaskId, body.Instructions, body.AllowedMcpTools, body.AllowedPaths)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> StartActorSession(Guid taskId, Guid subtaskId, [FromBody] ActorSessionBody body, StartActorSessionHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId || subtaskId != body.SubtaskId)
            return ProblemResults.Bad("TaskId/SubtaskId mismatch.");

        var result = await handler.HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(body.TaskId, body.SubtaskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> LaunchSession(Guid sessionId, [FromBody] LaunchSessionBody body, LaunchSessionHandler handler, CancellationToken ct)
    {
        if (sessionId != body.SessionId)
            return ProblemResults.Bad("SessionId mismatch.");

        var result = await handler.HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(body.SessionId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> ReportSubtaskResult(Guid taskId, Guid subtaskId, [FromBody] SubtaskResultBody body, ReportSubtaskResultHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId || subtaskId != body.SubtaskId)
            return ProblemResults.Bad("TaskId/SubtaskId mismatch.");

        var result = await handler.HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(
            body.TaskId, body.SubtaskId, body.DiffRef, body.Summary)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> FailSubtask(Guid taskId, Guid subtaskId, [FromBody] SubtaskFailBody body, FailSubtaskHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId || subtaskId != body.SubtaskId)
            return ProblemResults.Bad("TaskId/SubtaskId mismatch.");

        var result = await handler.HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(
            body.TaskId, body.SubtaskId, body.Reason)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> StartReview(Guid taskId, [FromBody] PipelineStartBody body, StartReviewHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new StartReviewCommand(new StartReviewRequest(body.TaskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> RecordReviewVerdict(Guid taskId, [FromBody] VerdictBody body, RecordReviewVerdictHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(
            body.TaskId, body.Kind, body.Note, body.SubtaskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> RecordReviewCheck(Guid taskId, [FromBody] ReviewCheckBody body, HttpContext ctx, RecordReviewCheckHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var actor = ctx.GetActor();
        if (actor.UserId is null)
            return ProblemResults.Unauthorized();

        var result = await handler.HandleAsync(new RecordReviewCheckCommand(new RecordReviewCheckRequest(
            body.TaskId, body.DeviceId, actor.UserId!.Value, body.CheckCommand, body.Path)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> ApprovePipeline(Guid taskId, [FromBody] ApproveBody body, ApprovePipelineHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new ApprovePipelineCommand(new ApprovePipelineRequest(body.TaskId, body.Note)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> ForceClosePipeline(Guid taskId, [FromBody] ForceCloseBody body, HttpContext ctx, ForceClosePipelineHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var actor = ctx.GetActor();
        if (actor.UserId is null)
            return ProblemResults.Unauthorized();

        var result = await handler.HandleAsync(new ForceClosePipelineCommand(new ForceClosePipelineRequest(body.TaskId, actor.UserId!.Value.ToString(), body.Reason)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    /// <summary>JSON body for pipeline start and review start. <c>TaskId</c> must match the route.</summary>
    public record PipelineStartBody(Guid TaskId);

    /// <summary>JSON body for <c>POST /v1/sessions/{sessionId:guid}/launch</c>. <c>SessionId</c> must match the route.</summary>
    public record LaunchSessionBody(Guid SessionId);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/subtasks</c>.</summary>
    public record CreateSubtaskBody(
        Guid TaskId,
        string Instructions,
        IReadOnlyList<string>? AllowedMcpTools = null,
        IReadOnlyList<string>? AllowedPaths = null);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/session</c>.</summary>
    public record ActorSessionBody(Guid TaskId, Guid SubtaskId);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/result</c>.</summary>
    public record SubtaskResultBody(Guid TaskId, Guid SubtaskId, string DiffRef, string Summary);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/fail</c>.</summary>
    public record SubtaskFailBody(Guid TaskId, Guid SubtaskId, string Reason);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/pipeline/verdict</c>.</summary>
    public record VerdictBody(Guid TaskId, ReviewVerdictKind Kind, string Note, Guid? SubtaskId = null);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/pipeline/review/check</c>. The acting user is <c>HttpContext.GetActor().UserId</c>, not a field.</summary>
    public record ReviewCheckBody(Guid TaskId, Guid DeviceId, string CheckCommand, string? Path = null);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/pipeline/approve</c>.</summary>
    public record ApproveBody(Guid TaskId, string? Note = null);

    /// <summary>
    /// JSON body for <c>POST /v1/tasks/{taskId:guid}/pipeline/force-close</c>: task id and an optional reason.
    /// The route requires <c>RequireHumanOrApiToken(ApiTokenCapability.Admin)</c> for tokens.
    /// </summary>
    public record ForceCloseBody(Guid TaskId, string? Reason = null);
}
