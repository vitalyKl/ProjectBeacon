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
    /// Maps <c>POST /v1/tasks/{taskId:guid}/pipeline/start</c>, <c>POST /v1/tasks/{taskId:guid}/pipeline/cancel</c>, <c>POST /v1/tasks/{taskId:guid}/pipeline/restart</c>, <c>POST /v1/tasks/{taskId:guid}/pipeline/sessions/{sessionId:guid}/cancel</c>, and <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/cancel</c>, <c>POST /v1/tasks/{taskId:guid}/subtasks</c>, <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/session</c>, <c>POST /v1/sessions/{sessionId:guid}/launch</c>, <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/result</c>, <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/fail</c>, <c>POST /v1/tasks/{taskId:guid}/pipeline/review/start</c>, <c>POST /v1/tasks/{taskId:guid}/pipeline/verdict</c>, <c>POST /v1/tasks/{taskId:guid}/pipeline/review/check</c>, and the pipeline drive, session, subtask, and review-verdict routes with <c>RequireHumanOrApiToken(ApiTokenCapability.SessionDrive)</c>. Approval is Human-only; a reviewer token may submit a verdict but cannot approve.
    /// Maps <c>POST /v1/tasks/{taskId:guid}/pipeline/force-close</c> with <c>RequireHumanOrApiToken(ApiTokenCapability.Admin)</c>. The body is task id plus an optional reason.
    /// </summary>
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/tasks/{taskId:guid}/pipeline", GetPipeline).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/sessions/{sessionId:guid}/forms/{formId}/reply", ReplyPipelineForm).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/start", StartPipeline).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/cancel", CancelPipeline).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/restart", RestartPipeline).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/sessions/{sessionId:guid}/cancel", CancelPipelineSession).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/cancel", CancelSubtask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);

        app.MapPost("/v1/tasks/{taskId:guid}/subtasks", CreateSubtask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/session", StartActorSession).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/sessions/{sessionId:guid}/launch", LaunchSession).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/sessions/{sessionId:guid}/retry", RetryPipelineSession).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/result", ReportSubtaskResult).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/fail", FailSubtask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/review/start", StartReview).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/verdict", RecordReviewVerdict).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/review/check", RecordReviewCheck).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.SessionDrive);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/approve", ApprovePipeline).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/force-close", ForceClosePipeline).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.Admin);

        return app;
    }

    private static async Task<IResult> ReplyPipelineForm(
        Guid taskId,
        Guid sessionId,
        string formId,
        [FromBody] PipelineFormReplyBody body,
        ReplyPipelineFormHandler handler,
        HttpContext ctx,
        CancellationToken ct)
    {
        var actor = ctx.GetActor();
        if (actor.UserId is not Guid userId)
            return ProblemResults.Unauthorized();

        var result = await handler.HandleAsync(new ReplyPipelineFormCommand(
            new ReplyPipelineFormRequest(taskId, sessionId, userId, formId, body.AnswerJson)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> GetPipeline(Guid taskId, GetPipelineHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetPipelineCommand(new GetPipelineRequest(taskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult(404);
    }

    private static async Task<IResult> StartPipeline(Guid taskId, [FromBody] PipelineStartBody body, HttpContext ctx, StartPipelineHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new StartPipelineCommand(new StartPipelineRequest(body.TaskId), ctx.GetActor()), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> CancelPipeline(
        Guid taskId,
        [FromBody] PipelineControlBody body,
        CancelPipelineHandler handler,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new CancelPipelineCommand(new CancelPipelineRequest(taskId), ctx.GetActor()), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> RestartPipeline(
        Guid taskId,
        [FromBody] PipelineControlBody body,
        RestartPipelineHandler handler,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new RestartPipelineCommand(new RestartPipelineRequest(taskId), ctx.GetActor()), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> CancelPipelineSession(
        Guid taskId,
        Guid sessionId,
        [FromBody] PipelineSessionCancelBody body,
        CancelPipelineSessionHandler handler,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (taskId != body.TaskId || sessionId != body.SessionId)
            return ProblemResults.Bad("TaskId/SessionId mismatch.");

        var result = await handler.HandleAsync(
            new CancelPipelineSessionCommand(new CancelPipelineSessionRequest(taskId, sessionId), ctx.GetActor()), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> CancelSubtask(
        Guid taskId,
        Guid subtaskId,
        [FromBody] SubtaskCancelBody body,
        CancelSubtaskHandler handler,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (taskId != body.TaskId || subtaskId != body.SubtaskId)
            return ProblemResults.Bad("TaskId/SubtaskId mismatch.");

        var result = await handler.HandleAsync(
            new CancelSubtaskCommand(new CancelSubtaskRequest(taskId, subtaskId), ctx.GetActor()), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> CreateSubtask(Guid taskId, [FromBody] CreateSubtaskBody body, HttpContext context, CreateSubtaskHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            body.TaskId, body.Instructions, body.AllowedMcpTools, body.AllowedPaths,
            body.Profile, body.WakePlannerAfter, body.OptionalMcpTools), context.GetActor()), ct);
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

    private static async Task<IResult> RetryPipelineSession(Guid sessionId, RetryPipelineSessionHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new RetryPipelineSessionCommand(new RetryPipelineSessionRequest(sessionId)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> ReportSubtaskResult(Guid taskId, Guid subtaskId, [FromBody] SubtaskResultBody body, HttpContext ctx, ReportSubtaskResultHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId || subtaskId != body.SubtaskId)
            return ProblemResults.Bad("TaskId/SubtaskId mismatch.");

        var result = await handler.HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(
            body.TaskId, body.SubtaskId, body.DiffRef, body.Summary, body.Abstract), ctx.GetActor()), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> FailSubtask(Guid taskId, Guid subtaskId, [FromBody] SubtaskFailBody body, HttpContext ctx, FailSubtaskHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId || subtaskId != body.SubtaskId)
            return ProblemResults.Bad("TaskId/SubtaskId mismatch.");

        var result = await handler.HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(
            body.TaskId, body.SubtaskId, body.Reason, body.Abstract), ctx.GetActor()), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> StartReview(Guid taskId, [FromBody] PipelineStartBody body, HttpContext context, StartReviewHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new StartReviewCommand(new StartReviewRequest(body.TaskId,
                AutoLaunch: string.Equals(context.User.FindFirst("pipeline_role")?.Value, PipelineRole.Planner.ToString(), StringComparison.Ordinal)), context.GetActor()), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> RecordReviewVerdict(Guid taskId, [FromBody] VerdictBody body, HttpContext ctx, RecordReviewVerdictHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(
            body.TaskId, body.Kind, body.Note, body.SubtaskId), ctx.GetActor()), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> RecordReviewCheck(Guid taskId, [FromBody] ReviewCheckBody body, HttpContext ctx, RecordReviewCheckHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var actor = ctx.GetActor();
        // Reviewer binding and device ownership both need a user id; tokens without a creator cannot be bound.
        if (actor.UserId is null)
            return ProblemResults.Unauthorized();

        var result = await handler.HandleAsync(new RecordReviewCheckCommand(
            new RecordReviewCheckRequest(body.TaskId, body.DeviceId, actor.UserId.Value, body.CheckCommand, body.Path),
            actor), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> ApprovePipeline(Guid taskId, [FromBody] ApproveBody body, HttpContext ctx, ApprovePipelineHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new ApprovePipelineCommand(new ApprovePipelineRequest(body.TaskId, body.Note), ctx.GetActor()), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> ForceClosePipeline(Guid taskId, [FromBody] ForceCloseBody body, HttpContext ctx, ForceClosePipelineHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var actor = ctx.GetActor();
        var actorId = actor.UserId?.ToString() ?? actor.TokenId?.ToString() ?? string.Empty;

        var result = await handler.HandleAsync(
            new ForceClosePipelineCommand(
                new ForceClosePipelineRequest(body.TaskId, actorId, body.Reason), actor), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    /// <summary>JSON body for replying to a task pipeline form.</summary>
    public record PipelineFormReplyBody(string AnswerJson);

    /// <summary>JSON body for pipeline cancellation or restart.</summary>
    public record PipelineControlBody(Guid TaskId);

    /// <summary>JSON body for cancelling a pipeline session.</summary>
    public record PipelineSessionCancelBody(Guid TaskId, Guid SessionId);

    /// <summary>JSON body for cancelling a subtask.</summary>
    public record SubtaskCancelBody(Guid TaskId, Guid SubtaskId);

    /// <summary>JSON body for pipeline start and review start. <c>TaskId</c> must match the route.</summary>
    public record PipelineStartBody(Guid TaskId);

    /// <summary>JSON body for <c>POST /v1/sessions/{sessionId:guid}/launch</c>. <c>SessionId</c> must match the route.</summary>
    public record LaunchSessionBody(Guid SessionId);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/subtasks</c>.</summary>
    public record CreateSubtaskBody(
        Guid TaskId,
        string Instructions,
        IReadOnlyList<string>? AllowedMcpTools = null,
        IReadOnlyList<string>? AllowedPaths = null,
        string? Profile = null,
        bool WakePlannerAfter = false,
        IReadOnlyList<string>? OptionalMcpTools = null);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/session</c>.</summary>
    public record ActorSessionBody(Guid TaskId, Guid SubtaskId);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/result</c>.</summary>
    public record SubtaskResultBody(Guid TaskId, Guid SubtaskId, string DiffRef, string Summary, string? Abstract = null);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/fail</c>.</summary>
    public record SubtaskFailBody(Guid TaskId, Guid SubtaskId, string Reason, string? Abstract = null);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/pipeline/verdict</c>.</summary>
    public record VerdictBody(Guid TaskId, ReviewVerdictKind Kind, string Note, Guid? SubtaskId = null);

    /// <summary>
    /// JSON body for <c>POST /v1/tasks/{taskId:guid}/pipeline/review/check</c>. The acting user is
    /// <c>HttpContext.GetActor().UserId</c>, not a field. CheckCommand is required for human actors;
    /// API tokens must omit it and the project's pinned ReviewCheckCommand is used instead.
    /// </summary>
    public record ReviewCheckBody(Guid TaskId, Guid DeviceId, string? CheckCommand, string? Path = null);

    /// <summary>JSON body for <c>POST /v1/tasks/{taskId:guid}/pipeline/approve</c>.</summary>
    public record ApproveBody(Guid TaskId, string? Note = null);

    /// <summary>
    /// JSON body for <c>POST /v1/tasks/{taskId:guid}/pipeline/force-close</c>: task id and an optional reason.
    /// The route requires <c>RequireHumanOrApiToken(ApiTokenCapability.Admin)</c> for tokens.
    /// </summary>
    public record ForceCloseBody(Guid TaskId, string? Reason = null);
}
