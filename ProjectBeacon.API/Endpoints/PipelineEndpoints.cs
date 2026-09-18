namespace ProjectBeacon.API.Endpoints;

using Application.Common;
using Application.Tasks;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API.Auth;

public static class PipelineEndpoints
{
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/tasks/{taskId:guid}/pipeline", GetPipeline).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskRead);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/start", StartPipeline).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks", CreateSubtask).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/session", StartActorSession).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/sessions/{sessionId:guid}/launch", LaunchSession).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/result", ReportSubtaskResult).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/subtasks/{subtaskId:guid}/fail", FailSubtask).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/review/start", StartReview).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/verdict", RecordReviewVerdict).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/approve", ApprovePipeline).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/pipeline/force-close", ForceClosePipeline).RequireAuthorization().DisableAntiforgery().RequireCapability(ApiTokenCapability.Admin);

        return app;
    }

    private static async Task<IResult> GetPipeline(Guid taskId, GetPipelineHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetPipelineCommand(new GetPipelineRequest(taskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> StartPipeline(Guid taskId, [FromBody] PipelineStartBody body, StartPipelineHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return Results.BadRequest("TaskId mismatch.");

        var result = await handler.HandleAsync(new StartPipelineCommand(new StartPipelineRequest(body.TaskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> CreateSubtask(Guid taskId, [FromBody] CreateSubtaskBody body, CreateSubtaskHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return Results.BadRequest("TaskId mismatch.");

        var result = await handler.HandleAsync(new CreateSubtaskCommand(new CreateSubtaskRequest(
            body.TaskId, body.Instructions, body.AllowedMcpTools, body.AllowedPaths)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> StartActorSession(Guid taskId, Guid subtaskId, [FromBody] ActorSessionBody body, StartActorSessionHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId || subtaskId != body.SubtaskId)
            return Results.BadRequest("TaskId/SubtaskId mismatch.");

        var result = await handler.HandleAsync(new StartActorSessionCommand(new StartActorSessionRequest(body.TaskId, body.SubtaskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> LaunchSession(Guid sessionId, [FromBody] LaunchSessionBody body, LaunchSessionHandler handler, CancellationToken ct)
    {
        if (sessionId != body.SessionId)
            return Results.BadRequest("SessionId mismatch.");

        var result = await handler.HandleAsync(new LaunchSessionCommand(new LaunchSessionRequest(body.SessionId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> ReportSubtaskResult(Guid taskId, Guid subtaskId, [FromBody] SubtaskResultBody body, ReportSubtaskResultHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId || subtaskId != body.SubtaskId)
            return Results.BadRequest("TaskId/SubtaskId mismatch.");

        var result = await handler.HandleAsync(new ReportSubtaskResultCommand(new ReportSubtaskResultRequest(
            body.TaskId, body.SubtaskId, body.DiffRef, body.Summary)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> FailSubtask(Guid taskId, Guid subtaskId, [FromBody] SubtaskFailBody body, FailSubtaskHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId || subtaskId != body.SubtaskId)
            return Results.BadRequest("TaskId/SubtaskId mismatch.");

        var result = await handler.HandleAsync(new FailSubtaskCommand(new FailSubtaskRequest(
            body.TaskId, body.SubtaskId, body.Reason)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> StartReview(Guid taskId, [FromBody] PipelineStartBody body, StartReviewHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return Results.BadRequest("TaskId mismatch.");

        var result = await handler.HandleAsync(new StartReviewCommand(new StartReviewRequest(body.TaskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> RecordReviewVerdict(Guid taskId, [FromBody] VerdictBody body, RecordReviewVerdictHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return Results.BadRequest("TaskId mismatch.");

        var result = await handler.HandleAsync(new RecordReviewVerdictCommand(new RecordReviewVerdictRequest(
            body.TaskId, body.Kind, body.Note, body.SubtaskId)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> ApprovePipeline(Guid taskId, [FromBody] ApproveBody body, ApprovePipelineHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return Results.BadRequest("TaskId mismatch.");

        var result = await handler.HandleAsync(new ApprovePipelineCommand(new ApprovePipelineRequest(body.TaskId, body.Note)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> ForceClosePipeline(Guid taskId, [FromBody] ForceCloseBody body, ForceClosePipelineHandler handler, CancellationToken ct)
    {
        if (taskId != body.TaskId)
            return Results.BadRequest("TaskId mismatch.");

        var result = await handler.HandleAsync(new ForceClosePipelineCommand(new ForceClosePipelineRequest(body.TaskId, body.ActorId, body.Reason)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    public record PipelineStartBody(Guid TaskId);

    public record LaunchSessionBody(Guid SessionId);

    public record CreateSubtaskBody(
        Guid TaskId,
        string Instructions,
        IReadOnlyList<string>? AllowedMcpTools = null,
        IReadOnlyList<string>? AllowedPaths = null);

    public record ActorSessionBody(Guid TaskId, Guid SubtaskId);

    public record SubtaskResultBody(Guid TaskId, Guid SubtaskId, string DiffRef, string Summary);

    public record SubtaskFailBody(Guid TaskId, Guid SubtaskId, string Reason);

    public record VerdictBody(Guid TaskId, ReviewVerdictKind Kind, string Note, Guid? SubtaskId = null);

    public record ApproveBody(Guid TaskId, string? Note = null);

    public record ForceCloseBody(Guid TaskId, string ActorId, string? Reason = null);
}
