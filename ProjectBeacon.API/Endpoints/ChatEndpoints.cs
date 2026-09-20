namespace ProjectBeacon.API.Endpoints;

using System.Security.Claims;
using Application.Chat;
using Microsoft.AspNetCore.Mvc;

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/chat/sessions", ListSessions).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/chat/sessions", CreateSession).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/chat/sessions/{id:guid}", GetSession).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/chat/sessions/{id:guid}/parts", ListParts).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/chat/sessions/{id:guid}/prompt", SendPrompt).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/chat/sessions/{id:guid}/abort", Abort).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/chat/sessions/{id:guid}/parts", AppendPart).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/chat/sessions/{id:guid}/idle", MarkIdle).RequireAuthorization().DisableAntiforgery();
        return app;
    }

    public record CreateBody(string? Title);
    public record PromptBody(string Text, string? Model);
    public record AppendBody(string Role, string Kind, string Body, string? ExternalId);

    private static async Task<IResult> ListSessions(ListChatSessionsHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId))
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new ListChatSessionsCommand(new ListChatSessionsRequest(userId)), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> CreateSession([FromBody] CreateBody? body, CreateChatSessionHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId))
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new CreateChatSessionCommand(new CreateChatSessionRequest(userId, body?.Title)), ct);
        return result.Success ? Results.Created($"/v1/chat/sessions/{result.Value!.Id}", result.Value) : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> GetSession(Guid id, GetChatSessionHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId))
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new GetChatSessionCommand(new GetChatSessionRequest(id, userId)), ct);
        return result.Success ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> ListParts(Guid id, ListChatPartsHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId))
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new ListChatPartsCommand(new ListChatPartsRequest(id, userId)), ct);
        return result.Success ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> SendPrompt(Guid id, [FromBody] PromptBody body, SendChatPromptHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId))
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new SendChatPromptCommand(new SendChatPromptRequest(id, userId, body.Text, body.Model)), ct);
        return result.Success ? Results.Accepted($"/v1/chat/sessions/{id}", result.Value) : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> Abort(Guid id, AbortChatHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId))
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new AbortChatCommand(new AbortChatRequest(id, userId)), ct);
        return result.Success ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> AppendPart(Guid id, [FromBody] AppendBody body, AppendChatPartHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryDeviceId(user, out var deviceId))
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new AppendChatPartCommand(new AppendChatPartRequest(
            id, deviceId, body.Role, body.Kind, body.Body, body.ExternalId)), ct);
        return result.Success ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> MarkIdle(Guid id, MarkChatIdleHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryDeviceId(user, out var deviceId))
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new MarkChatIdleCommand(new MarkChatIdleRequest(id, deviceId)), ct);
        return result.Success ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
    }

    private static bool TryUserId(ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;
        return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }

    private static bool TryDeviceId(ClaimsPrincipal user, out Guid deviceId)
    {
        deviceId = Guid.Empty;
        return user.Identity?.AuthenticationType == "DeviceToken"
            && Guid.TryParse(user.FindFirstValue("device_id"), out deviceId);
    }
}
