namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;

using Application.Agents;
using Application.Devices;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API.Auth;

/// <summary>Workstation enroll, heartbeat, command queue, and per-device project roots.</summary>
public static class DeviceEndpoints
{
    /// <summary>
    /// <c>RequireDevice</c> covers heartbeat, claim, complete, llama-swap config, and OpenCode connections.
    /// Create, list, revoke, enqueue, and runtime attach are <c>RequireHuman</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/devices", CreateDevice).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/devices", ListDevices).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/devices/{id:guid}", RevokeDevice).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/devices/me/heartbeat", Heartbeat).RequireAuthorization().DisableAntiforgery().RequireDevice();
        app.MapGet("/v1/devices/me/commands", ClaimCommand).RequireAuthorization().DisableAntiforgery().RequireDevice();
        app.MapPost("/v1/commands/{id:guid}/complete", CompleteCommand).RequireAuthorization().DisableAntiforgery().RequireDevice();
        app.MapPost("/v1/devices/{id:guid}/commands", EnqueueCommand).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/commands/{id:guid}", GetCommand).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/runtimes", ListRuntimes).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/runtimes", AttachRuntime).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/projects/{projectId:guid}/runtimes/{id:guid}", DetachRuntime).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/devices/me/llamaswap-config", GetLlamaSwapConfig).RequireAuthorization().DisableAntiforgery().RequireDevice();
        app.MapGet("/v1/devices/me/opencode-connections", GetOpenCodeConnections).RequireAuthorization().DisableAntiforgery().RequireDevice();
        return app;
    }
    /// <summary>
    /// Body for enrolling a device: name and fingerprint.
    /// </summary>
    public record CreateDeviceBody(string Name, string Fingerprint);
    /// <summary>
    /// Body for a device heartbeat: optional probe JSON and workstation JSON.
    /// </summary>
    public record HeartbeatBody(string? ProbeJson, string? WorkstationJson);
    /// <summary>
    /// Body for queueing a device command. Kind is a WorkstationCommandKind name.
    /// </summary>
    public record EnqueueBody(WorkstationCommandKind Kind, string? PayloadJson, Guid? ProjectId);
    /// <summary>
    /// Body for finishing a device command: success, optional result JSON, and optional error.
    /// </summary>
    public record CompleteBody(bool Success, string? ResultJson, string? Error);
    /// <summary>
    /// Body for binding a project to a device and a local root.
    /// </summary>
    public record AttachRuntimeBody(Guid DeviceId, string LocalRoot);

    private static async Task<IResult> CreateDevice(
        [FromBody] CreateDeviceBody body, CreateDeviceHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest(body.Name, body.Fingerprint, actor.UserId!.Value)), ct);
        return result.Success
            ? Results.Created($"/v1/devices/{result.Value!.Id}", result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> ListDevices(ListDevicesHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new ListDevicesCommand(new ListDevicesRequest(actor.UserId!.Value)), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> RevokeDevice(
        Guid id, RevokeDeviceHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new RevokeDeviceCommand(new RevokeDeviceRequest(id, actor.UserId!.Value)), ct);
        return result.Success ? Results.NoContent() : result.FromResult(404);
    }

    private static async Task<IResult> Heartbeat(
        [FromBody] HeartbeatBody? body, HeartbeatDeviceHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(actor.DeviceId!.Value, body?.ProbeJson, body?.WorkstationJson)), ct);
        return result.Success ? Results.Ok(result.Value) : ProblemResults.Unauthorized();
    }

    private static async Task<IResult> ClaimCommand(
        ClaimNextCommandHandler handler, HttpContext ctx, int? wait, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var seconds = wait is > 0 and <= 30 ? wait.Value : 0;
        var result = await handler.HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(actor.DeviceId!.Value, TimeSpan.FromSeconds(seconds))), ct);
        return result.Value is null ? Results.NoContent() : Results.Ok(result.Value);
    }

    private static async Task<IResult> CompleteCommand(
        Guid id, [FromBody] CompleteBody body, CompleteCommandHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(id, actor.DeviceId!.Value, body.Success, body.ResultJson, body.Error)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> EnqueueCommand(
        Guid id, [FromBody] EnqueueBody body, EnqueueCommandHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(id, actor.UserId!.Value, body.Kind, body.PayloadJson, body.ProjectId)), ct);
        if (!result.Success)
        {
            return result.FromResult();
        }
        return Results.Accepted($"/v1/commands/{result.Value!.Id}", result.Value);
    }

    private static async Task<IResult> GetCommand(
        Guid id, GetCommandHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new GetCommandCommand(new GetCommandRequest(id, actor.UserId!.Value)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> ListRuntimes(
        Guid projectId, ListRuntimesHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new ListRuntimesCommand(new ListRuntimesRequest(projectId, actor.UserId!.Value)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> AttachRuntime(
        Guid projectId, [FromBody] AttachRuntimeBody body, AttachRuntimeHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(projectId, body.DeviceId, actor.UserId!.Value, body.LocalRoot)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> DetachRuntime(
        Guid projectId, Guid id, DetachRuntimeHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new DetachRuntimeCommand(new DetachRuntimeRequest(id, actor.UserId!.Value)), ct);
        return result.Success ? Results.NoContent() : result.FromResult(404);
    }

    private static async Task<IResult> GetOpenCodeConnections(
        DeviceOpenCodeConnectionsHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new DeviceOpenCodeConnectionsCommand(actor.DeviceId!.Value), ct);
        return result.Success ? Results.Ok(result.Value) : ProblemResults.Unauthorized();
    }

    private static async Task<IResult> GetLlamaSwapConfig(
        GetLlamaSwapConfigHandler handler, HttpContext ctx, CancellationToken ct)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new GetLlamaSwapConfigCommand(new GetLlamaSwapConfigRequest(actor.DeviceId!.Value)), ct);
        return result.Success ? Results.Ok(result.Value) : ProblemResults.Unauthorized();
    }
}
