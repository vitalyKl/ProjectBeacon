namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;

using Application.Common;
using Application.Tasks;
using Application.Projects; // ClaimTaskHandler
using Infrastructure.Data;
using Domain.Entities.Projects;
using Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API.Auth;
using Microsoft.EntityFrameworkCore;

public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects/{projectId:guid}/tasks", CreateTask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPut("/v1/projects/{projectId:guid}/tasks/{taskId:guid}", UpdateTask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPut("/v1/tasks/{taskId:guid}", UpdateTask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapDelete("/v1/projects/{projectId:guid}/tasks/{taskId:guid}", DeleteTask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapDelete("/v1/tasks/{taskId:guid}", DeleteTask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapGet("/v1/projects/{projectId:guid}/tasks", ListTasks).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapGet("/v1/projects/{projectId:guid}/tasks/{taskId:guid}", GetTaskInProject).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapGet("/v1/tasks/{taskId:guid}", GetTask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapPatch("/v1/projects/{projectId:guid}/tasks/{taskId:guid}/status", ChangeStatusInProject).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPatch("/v1/tasks/{taskId:guid}/status", ChangeStatus).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPatch("/v1/projects/{projectId:guid}/tasks/{taskId:guid}/substage", ChangeSubStage).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPatch("/v1/tasks/{taskId:guid}/substage", ChangeSubStage).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPatch("/v1/projects/{projectId:guid}/tasks/{taskId:guid}/claim", ClaimTask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPatch("/v1/tasks/{taskId:guid}/claim", ClaimTask).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/projects/{projectId:guid}/tasks/{taskId:guid}/comments", AddComment).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPost("/v1/tasks/{taskId:guid}/comments", AddComment).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPut("/v1/projects/{projectId:guid}/tasks/{taskId:guid}/dependencies", SetDependencies).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPut("/v1/tasks/{taskId:guid}/dependencies", SetDependencies).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPatch("/v1/projects/{projectId:guid}/tasks/{taskId:guid}/review-notes", AddReviewNotes).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPatch("/v1/tasks/{taskId:guid}/review-notes", AddReviewNotes).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapGet("/v1/tasks/{taskId:guid}/steps", ListSteps).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskRead);
        app.MapPost("/v1/tasks/{taskId:guid}/steps", AddStep).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapPatch("/v1/steps/{stepId:guid}", ToggleStep).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);
        app.MapDelete("/v1/steps/{stepId:guid}", DeleteStep).RequireAuthorization().DisableAntiforgery().RequireHumanOrApiToken(ApiTokenCapability.TaskWrite);

        return app;
    }

    private static async Task<IResult> CreateTask(Guid projectId, [FromBody] CreateTaskRequest request, HttpContext ctx, CreateTaskHandler handler)
    {
        if (projectId != request.ProjectId)
            return ProblemResults.Bad("ProjectId mismatch.");

        var result = await handler.HandleAsync(new CreateTaskCommand(request, ctx.GetActor()));

        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/tasks/{result.Value.Id}", MapTaskResponse(result.Value))
            : result.FromResult();
    }

    private static async Task<IResult> UpdateTask(Guid taskId, [FromBody] UpdateTaskRequest request, UpdateTaskHandler handler)
    {
        if (taskId != request.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new UpdateTaskCommand(request));

        return result.Success
            ? Results.Ok(MapTaskResponse(result.Value))
            : result.FromResult(404);
    }

    private static async Task<IResult> DeleteTask(Guid taskId, DeleteTaskHandler handler)
    {
        var result = await handler.HandleAsync(new DeleteTaskCommand(new DeleteTaskRequest(taskId)));

        return result.Success
            ? Results.NoContent()
            : result.FromResult(404);
    }

    private static async Task<IResult> ListTasks(Guid projectId, [FromQuery] string? status, CancellationToken ct, ListProjectTasksHandler handler)
    {
        var result = await handler.HandleAsync(new ListProjectTasksCommand(new ListProjectTasksRequest(projectId)), ct);

        if (!result.Success)
            return result.FromResult();

        var tasks = result.Value;
        if (status is not null)
        {
            tasks = tasks.Where(t => t.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return Results.Ok(tasks.Select(MapTaskResponse));
    }

    private static async Task<IResult> GetTask(Guid taskId, GetTaskHandler handler)
    {
        var result = await handler.HandleAsync(new GetTaskCommand(new GetTaskRequest(taskId)));

        return result.Success
            ? Results.Ok(MapTaskResponse(result.Value))
            : result.FromResult(404);
    }

    private static async Task<IResult> GetTaskInProject(Guid projectId, Guid taskId, GetTaskHandler handler)
    {
        var result = await handler.HandleAsync(new GetTaskCommand(new GetTaskRequest(taskId, projectId)));

        return result.Success
            ? Results.Ok(MapTaskResponse(result.Value))
            : result.FromResult(404);
    }

    private static async Task<IResult> ChangeStatus(Guid taskId, [FromBody] ChangeTaskStatusBody body, ChangeTaskStatusHandler handler)
    {
        var result = await handler.HandleAsync(new ChangeTaskStatusCommand(new ChangeTaskStatusRequest(taskId, body.Status)));
        return result.Success
            ? Results.Ok(MapTaskResponse(result.Value))
            : result.FromResult();
    }

    private static async Task<IResult> ChangeStatusInProject(Guid projectId, Guid taskId, [FromBody] ChangeTaskStatusBody body, ChangeTaskStatusHandler handler)
    {
        var result = await handler.HandleAsync(new ChangeTaskStatusCommand(new ChangeTaskStatusRequest(taskId, body.Status, projectId)));
        return result.Success
            ? Results.Ok(MapTaskResponse(result.Value))
            : result.FromResult();
    }

    public record ChangeTaskStatusBody(TaskItemStatus Status);

    private static async Task<IResult> ChangeSubStage(Guid taskId, [FromBody] TaskSubStage subStage, ChangeSubStageHandler handler)
    {
        var result = await handler.HandleAsync(new ChangeSubStageCommand(new ChangeSubStageRequest(taskId, subStage)));

        return result.Success
            ? Results.Ok(MapTaskResponse(result.Value))
            : result.FromResult();
    }

    private static async Task<IResult> ClaimTask(Guid projectId, Guid taskId, ITenantContext tenant, ClaimTaskHandler handler)
    {
        var targetProject = projectId != Guid.Empty ? projectId : tenant.ProjectId ?? Guid.Empty;
        var result = await handler.HandleAsync(new ClaimTaskCommand(new ClaimTaskRequest(taskId, Guid.Empty, targetProject)));

        return result.Success
            ? Results.Ok(MapTaskResponse(result.Value))
            : result.FromResult(404);
    }

    private static async Task<IResult> AddComment(Guid taskId, [FromBody] AddCommentRequest request, HttpContext ctx, AddCommentHandler handler)
    {
        if (taskId != request.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new AddCommentCommand(request, ctx.GetActor()));

        return result.Success
            ? Results.Created($"/v1/tasks/{taskId}/comments/{result.Value.Id}", MapCommentResponse(result.Value))
            : result.FromResult();
    }

    private static async Task<IResult> SetDependencies(Guid taskId, [FromBody] SetDependenciesRequest request, SetDependenciesHandler handler)
    {
        if (taskId != request.TaskId)
            return ProblemResults.Bad("TaskId mismatch.");

        var result = await handler.HandleAsync(new SetDependenciesCommand(request));

        return result.Success
            ? Results.Ok(MapTaskResponse(result.Value))
            : result.FromResult();
    }

    private static async Task<IResult> AddReviewNotes(Guid taskId, [FromBody] string reviewNotes, AddReviewNotesHandler handler)
    {
        var result = await handler.HandleAsync(new AddReviewNotesCommand(new AddReviewNotesRequest(taskId, reviewNotes)));

        return result.Success
            ? Results.Ok(MapTaskResponse(result.Value))
            : result.FromResult();
    }

    private static TaskItemDto MapTaskResponse(TaskItemDto dto) =>
        new(dto.Id, dto.Title, dto.Description, dto.Status, dto.Priority, dto.Type, dto.SubStage,
            dto.ProjectId, dto.LabelId, dto.MilestoneId, dto.ReviewNotes, dto.CreatedAt, dto.CompletedAt,
            dto.Comments, dto.Dependencies);

    private static TaskCommentDto MapCommentResponse(TaskCommentDto dto) =>
        new(dto.Id, dto.Content, dto.UserId, dto.CreatedAt, dto.UpdatedAt);

    private static async Task<IResult> ListSteps(Guid taskId, ListTaskStepsHandler handler)
    {
        var result = await handler.HandleAsync(new ListTaskStepsCommand(new ListTaskStepsRequest(taskId)));
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    public record AddStepBody(string Title);

    private static async Task<IResult> AddStep(Guid taskId, [FromBody] AddStepBody body, AddTaskStepHandler handler)
    {
        var result = await handler.HandleAsync(new AddTaskStepCommand(new AddTaskStepRequest(taskId, body.Title)));
        return result.Success
            ? Results.Created($"/v1/steps/{result.Value!.Id}", result.Value)
            : result.FromResult();
    }

    public record ToggleStepBody(bool Done);

    private static async Task<IResult> ToggleStep(Guid stepId, [FromBody] ToggleStepBody body, ToggleTaskStepHandler handler)
    {
        var result = await handler.HandleAsync(new ToggleTaskStepCommand(new ToggleTaskStepRequest(stepId, body.Done)));
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> DeleteStep(Guid stepId, DeleteTaskStepHandler handler)
    {
        var result = await handler.HandleAsync(new DeleteTaskStepCommand(new DeleteTaskStepRequest(stepId)));
        return result.Success ? Results.NoContent() : result.FromResult(404);
    }
}
