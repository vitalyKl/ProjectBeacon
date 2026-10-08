namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

using Application.Chat;
using Microsoft.AspNetCore.Mvc;

/// <summary>Workstation chat routes. Humans read and prompt. The device appends parts and marks idle.</summary>
public static class ChatEndpoints
{
    /// <summary>
    /// Human routes under <c>/v1/chat/sessions</c> use <c>RequireHuman</c>.
    /// <c>POST .../parts</c> and <c>POST .../idle</c> use <c>RequireDevice</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/chat/sessions", ListSessions).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/chat/sessions", CreateSession).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/chat/sessions/{id:guid}", GetSession).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/chat/sessions/{id:guid}/parts", ListParts).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/chat/sessions/{id:guid}/prompt", SendPrompt).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/chat/sessions/{id:guid}/abort", Abort).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/chat/sessions/{id:guid}/parts", AppendPart).RequireAuthorization().DisableAntiforgery().RequireDevice();
        app.MapPost("/v1/chat/sessions/{id:guid}/idle", MarkIdle).RequireAuthorization().DisableAntiforgery().RequireDevice();
        return app;
    }
    /// <summary>
    /// Body for creating a chat session. Title is optional.
    /// </summary>
    public record CreateBody(string? Title);
    /// <summary>
    /// Body for sending a chat prompt: text and an optional model id.
    /// </summary>
    public record PromptBody(string Text, string? Model);
    /// <summary>
    /// Body a device posts for one chat part: role, kind, body, and an optional external id.
    /// </summary>
    public record AppendBody(string Role, string Kind, string Body, string? ExternalId);

    private static async Task<IResult> ListSessions(ListChatSessionsHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new ListChatSessionsCommand(new ListChatSessionsRequest(actor.UserId!.Value)), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> CreateSession([FromBody] CreateBody? body, CreateChatSessionHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new CreateChatSessionCommand(new CreateChatSessionRequest(actor.UserId!.Value, body?.Title)), ct);
        return result.Success ? Results.Created($"/v1/chat/sessions/{result.Value!.Id}", result.Value) : result.FromResult();
    }

    private static async Task<IResult> GetSession(Guid id, GetChatSessionHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new GetChatSessionCommand(new GetChatSessionRequest(id, actor.UserId!.Value)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> ListParts(Guid id, ListChatPartsHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new ListChatPartsCommand(new ListChatPartsRequest(id, actor.UserId!.Value)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> SendPrompt(Guid id, [FromBody] PromptBody body, SendChatPromptHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new SendChatPromptCommand(new SendChatPromptRequest(id, actor.UserId!.Value, body.Text, body.Model)), ct);
        return result.Success ? Results.Accepted($"/v1/chat/sessions/{id}", result.Value) : result.FromResult();
    }

    private static async Task<IResult> Abort(Guid id, AbortChatHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new AbortChatCommand(new AbortChatRequest(id, actor.UserId!.Value)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> AppendPart(Guid id, [FromBody] AppendBody body, AppendChatPartHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new AppendChatPartCommand(new AppendChatPartRequest(
            id, actor.DeviceId!.Value, body.Role, body.Kind, body.Body, body.ExternalId)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> MarkIdle(Guid id, MarkChatIdleHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new MarkChatIdleCommand(new MarkChatIdleRequest(id, actor.DeviceId!.Value)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }
}
