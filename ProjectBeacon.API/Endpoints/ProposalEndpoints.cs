namespace ProjectBeacon.API.Endpoints;

using System.Security.Claims;
using System.Text.Json;
using ProjectBeacon.API;
using ProjectBeacon.API.Auth;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Proposals;
using ProjectBeacon.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

/// <summary>HTTP API for reviewable, revision-bound proposals.</summary>
public static class ProposalEndpoints
{
    public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects/{projectId:guid}/proposals", Create)
            .RequireAuthorization().DisableAntiforgery()
            .RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapGet("/v1/projects/{projectId:guid}/proposals", List)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/proposals/mine", Mine)
            .RequireAuthorization().DisableAntiforgery()
            .RequireApiToken(ApiTokenCapability.ContextRead);
        app.MapPost("/v1/projects/{projectId:guid}/proposals/{proposalId:guid}/accept", Accept)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/proposals/{proposalId:guid}/reject", Reject)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/proposals/{proposalId:guid}/withdraw", Withdraw)
            .RequireAuthorization().DisableAntiforgery()
            .RequireHumanOrApiToken(ApiTokenCapability.ContextRead);
        app.MapGet("/v1/projects/{projectId:guid}/proposal-policies", ListPolicies)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPut("/v1/projects/{projectId:guid}/proposal-policies", SetPolicy)
            .RequireAuthorization().DisableAntiforgery().RequireHuman();
        return app;
    }

    private static async Task<IResult> Create(
        Guid projectId,
        [FromBody] CreateProposalBody body,
        HttpContext context,
        CreateProposalHandler handler,
        CancellationToken ct)
    {
        if (body.Payload.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { error = "Payload must be a JSON object." });

        var user = context.User;
        var command = new CreateProposalCommand(
            projectId,
            new CreateProposalRequest(
                body.TargetKind,
                body.TargetId,
                body.Operation,
                body.BaseRevision,
                body.Payload.GetRawText(),
                body.RationaleMarkdown,
                body.IdempotencyKey),
            context.GetActor(),
            ReadGuid(user, "pipeline_task_id"),
            ReadGuid(user, "pipeline_subtask_id"),
            ReadGuid(user, "pipeline_session_id"),
            user.FindFirst("pipeline_role")?.Value);

        var result = await handler.HandleAsync(command, ct);
        return result.Success
            ? Results.Created($"/v1/projects/{projectId}/proposals/{result.Value!.Id}", result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> List(
        Guid projectId,
        [FromQuery] ProposalStatus? status,
        [FromQuery] Guid? taskId,
        [FromQuery] int? limit,
        HttpContext context,
        ListProposalsHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new ListProposalsQuery(projectId, status, taskId, limit ?? 100),
            context.GetActor(),
            ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> Mine(
        Guid projectId,
        [FromQuery] ProposalStatus? status,
        HttpContext context,
        ListOwnProposalsHandler handler,
        CancellationToken ct)
    {
        var user = context.User;
        var result = await handler.HandleAsync(
            projectId,
            context.GetActor(),
            ReadGuid(user, "pipeline_task_id"),
            ReadGuid(user, "pipeline_subtask_id"),
            ReadGuid(user, "pipeline_session_id"),
            user.FindFirst("pipeline_role")?.Value,
            status,
            ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> Accept(
        Guid projectId,
        Guid proposalId,
        [FromBody] AcceptProposalBody body,
        HttpContext context,
        AcceptProposalHandler handler,
        CancellationToken ct)
    {
        if (body.AcceptedPayload.HasValue
            && body.AcceptedPayload.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            return Results.BadRequest(new { error = "AcceptedPayload must be a JSON object or null." });

        var acceptedPayload = body.AcceptedPayload.HasValue
            && body.AcceptedPayload.Value.ValueKind == JsonValueKind.Object
            ? body.AcceptedPayload.Value.GetRawText()
            : null;
        var result = await handler.HandleAsync(
            new AcceptProposalCommand(projectId, proposalId, context.GetActor(), acceptedPayload), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> Reject(
        Guid projectId,
        Guid proposalId,
        [FromBody] RejectProposalBody body,
        HttpContext context,
        RejectProposalHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new RejectProposalCommand(projectId, proposalId, context.GetActor(), body.Note), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> Withdraw(
        Guid projectId,
        Guid proposalId,
        HttpContext context,
        WithdrawProposalHandler handler,
        CancellationToken ct)
    {
        var user = context.User;
        var result = await handler.HandleAsync(new WithdrawProposalCommand(
            projectId,
            proposalId,
            context.GetActor(),
            ReadGuid(user, "pipeline_task_id"),
            ReadGuid(user, "pipeline_subtask_id"),
            ReadGuid(user, "pipeline_session_id"),
            user.FindFirst("pipeline_role")?.Value), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> ListPolicies(
        Guid projectId,
        HttpContext context,
        ListProposalPoliciesHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(projectId, context.GetActor(), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> SetPolicy(
        Guid projectId,
        [FromBody] SetProposalPolicyBody body,
        HttpContext context,
        SetProposalPolicyHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new SetProposalPolicyCommand(
            projectId,
            body.TargetKind,
            body.Operation,
            body.Mode,
            body.ExpectedPolicyRevision,
            context.GetActor()), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static Guid? ReadGuid(ClaimsPrincipal principal, string claim)
        => Guid.TryParse(principal.FindFirst(claim)?.Value, out var value) && value != Guid.Empty
            ? value
            : null;

    public sealed record CreateProposalBody(
        ContentTargetKind TargetKind,
        Guid TargetId,
        ProposalOperation Operation,
        int BaseRevision,
        JsonElement Payload,
        string RationaleMarkdown,
        string IdempotencyKey);

    public sealed record AcceptProposalBody(JsonElement? AcceptedPayload = null);
    public sealed record RejectProposalBody(string? Note = null);
    public sealed record SetProposalPolicyBody(
        ContentTargetKind TargetKind,
        ProposalOperation Operation,
        ProposalDecisionMode Mode,
        int ExpectedPolicyRevision);
}
