namespace ProjectBeacon.API.Endpoints;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        app.MapPatch("/v1/chat/sessions/{id:guid}", RenameSession).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/chat/sessions/{id:guid}/archive", ArchiveSession).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/chat/sessions/{id:guid}/parts", ListParts).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/chat/sessions/{id:guid}/events", ChatEvents).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/chat/sessions/{id:guid}/prompt", SendPrompt).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/chat/sessions/{id:guid}/abort", Abort).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/chat/sessions/{id:guid}/agent", SwitchAgent).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/chat/sessions/{id:guid}/forms/{formId}/reply", ReplyForm).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/chat/sessions/{id:guid}/parts", AppendPart).RequireAuthorization().DisableAntiforgery().RequireDevice();
        app.MapPost("/v1/chat/sessions/{id:guid}/idle", MarkIdle).RequireAuthorization().DisableAntiforgery().RequireDevice();
        app.MapPost("/v1/chat/sessions/{id:guid}/usage", ReportUsage).RequireAuthorization().DisableAntiforgery().RequireDevice();
        return app;
    }
    /// <summary>
    /// Body for creating a chat session. Title is optional.
    /// </summary>
    public record CreateBody(string? Title);
    /// <summary>
    /// Body for renaming a chat session.
    /// </summary>
    public record RenameBody(string Title);
    /// <summary>
    /// Body for sending a chat prompt: text and an optional model id.
    /// </summary>
    public record PromptBody(string Text, string? Model);
    /// <summary>
    /// Body for switching a chat session's agent.
    /// </summary>
    public record AgentBody(string Agent);
    /// <summary>
    /// Body for answering a model question: the raw JSON answer object.
    /// </summary>
    public record FormReplyBody(string AnswerJson);
    /// <summary>
    /// Body a device posts for one chat part: role, kind, body, an optional external id,
    /// and structured tool fields for tool call parts.
    /// </summary>
    /// <summary>
    /// Body a device posts for the session's token totals and the active model's context limit.
    /// </summary>
    public record UsageBody(int PromptTokens, int CompletionTokens, int AssistantMessages, double TotalCost, int? ContextLimit = null, double GenerationSeconds = 0);

    public record AppendBody(
        string Role,
        string Kind,
        string Body,
        string? ExternalId,
        string? Tool,
        string? ToolStatus,
        string? ToolInput,
        string? ToolResult,
        int? DurationMs,
        bool Complete = false);

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

    /// <summary>How long the event stream waits for an in-process signal before forcing a refresh.</summary>
    internal static TimeSpan EventWaitInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Thin SSE stream for a chat session. Emits a <c>ready</c> event, then a <c>changed</c> event on
    /// every part append, status change, or wait-interval timeout. There are no bare heartbeats:
    /// with multiple replicas a timeout is the only evidence that another replica may have written,
    /// so every wake announces a refresh. Clients refetch the parts through the REST list endpoint.
    /// </summary>
    private static async Task<IResult> ChatEvents(Guid id, GetChatSessionHandler handler, ChatEventSignal events, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        // Snapshot the change counter before reading the session status: a write landing in the
        // window between the snapshot and the status read must still be announced by a changed
        // event below, or a client that only consumes the ready event would miss it.
        var before = events.GetVersion(id);
        var result = await handler.HandleAsync(new GetChatSessionCommand(new GetChatSessionRequest(id, actor.UserId!.Value)), ct);
        if (!result.Success)
            return result.FromResult(404);

        // SSE must stream through reverse proxies without buffering or caching.
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter());
        await using var writer = new StreamWriter(ctx.Response.Body, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync("retry: 5000");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("event: ready");
        await writer.WriteLineAsync("data: " + JsonSerializer.Serialize(new { sessionId = id, status = result.Value!.Status }, json));
        await writer.WriteLineAsync();
        await writer.FlushAsync();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await events.WaitAsync(id, EventWaitInterval, ct);
                if (ct.IsCancellationRequested)
                    break;
                if (events.GetVersion(id) != before)
                    before = events.GetVersion(id);
                else
                {
                    // The wait interval elapsed without an in-process signal. With multiple replicas
                    // a write may have landed in another process, so announce a refresh instead of a
                    // heartbeat: the client refetches the parts and the REST listing is the source
                    // of truth.
                    before = events.GetVersion(id);
                }
                await writer.WriteLineAsync("event: changed");
                await writer.WriteLineAsync("data: " + JsonSerializer.Serialize(new { sessionId = id }, json));
                await writer.WriteLineAsync();
                await writer.FlushAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The client disconnected; the stream ends.
        }
        // Results.Empty, not NoContent: the stream has already written to the body, so setting a status code would throw.
        return Results.Empty;
    }

    private static async Task<IResult> RenameSession(Guid id, [FromBody] RenameBody body, RenameChatSessionHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new RenameChatSessionCommand(new RenameChatSessionRequest(id, actor.UserId!.Value, body.Title)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> ArchiveSession(Guid id, ArchiveChatSessionHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new ArchiveChatSessionCommand(new ArchiveChatSessionRequest(id, actor.UserId!.Value)), ct);
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

    private static async Task<IResult> SwitchAgent(Guid id, [FromBody] AgentBody body, SwitchChatAgentHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new SwitchChatAgentCommand(new SwitchChatAgentRequest(id, actor.UserId!.Value, body.Agent)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> ReplyForm(Guid id, string formId, [FromBody] FormReplyBody body, ReplyChatFormHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new ReplyChatFormCommand(new ReplyChatFormRequest(id, actor.UserId!.Value, formId, body.AnswerJson)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult();
    }

    private static async Task<IResult> AppendPart(Guid id, [FromBody] AppendBody body, AppendChatPartHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new AppendChatPartCommand(new AppendChatPartRequest(
            id, actor.DeviceId!.Value, body.Role, body.Kind, body.Body, body.ExternalId,
            body.Tool, body.ToolStatus, body.ToolInput, body.ToolResult, body.DurationMs, body.Complete)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> ReportUsage(Guid id, [FromBody] UsageBody body, ReportChatUsageHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new ReportChatUsageCommand(new ReportChatUsageRequest(
            id, actor.DeviceId!.Value, body.PromptTokens, body.CompletionTokens, body.AssistantMessages, body.TotalCost, body.ContextLimit, body.GenerationSeconds)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> MarkIdle(Guid id, MarkChatIdleHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new MarkChatIdleCommand(new MarkChatIdleRequest(id, actor.DeviceId!.Value)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }
}
