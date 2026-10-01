namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;

using System.Security.Claims;
using Application.Agents;
using Application.Devices;
using Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using ProjectBeacon.API.Auth;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/devices", CreateDevice).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/devices", ListDevices).RequireAuthorization().DisableAntiforgery();
        app.MapDelete("/v1/devices/{id:guid}", RevokeDevice).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/devices/me/heartbeat", Heartbeat).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/devices/me/commands", ClaimCommand).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/commands/{id:guid}/complete", CompleteCommand).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/devices/{id:guid}/commands", EnqueueCommand).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/commands/{id:guid}", GetCommand).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}/runtimes", ListRuntimes).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/projects/{projectId:guid}/runtimes", AttachRuntime).RequireAuthorization().DisableAntiforgery();
        app.MapDelete("/v1/projects/{projectId:guid}/runtimes/{id:guid}", DetachRuntime).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/devices/me/llamaswap-config", GetLlamaSwapConfig).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/devices/me/opencode-connections", GetOpenCodeConnections).RequireAuthorization().DisableAntiforgery();
        return app;
    }

    public record CreateDeviceBody(string Name, string Fingerprint);
    public record HeartbeatBody(string? ProbeJson, string? WorkstationJson);
    public record EnqueueBody(WorkstationCommandKind Kind, string? PayloadJson, Guid? ProjectId);
    public record CompleteBody(bool Success, string? ResultJson, string? Error);
    public record AttachRuntimeBody(Guid DeviceId, string LocalRoot);

    private static async Task<IResult> CreateDevice(
        [FromBody] CreateDeviceBody body, CreateDeviceHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new CreateDeviceCommand(new CreateDeviceRequest(body.Name, body.Fingerprint, userId)), ct);
        return result.Success
            ? Results.Created($"/v1/devices/{result.Value!.Id}", result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> ListDevices(ListDevicesHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new ListDevicesCommand(new ListDevicesRequest(userId)), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> RevokeDevice(
        Guid id, RevokeDeviceHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new RevokeDeviceCommand(new RevokeDeviceRequest(id, userId)), ct);
        return result.Success ? Results.NoContent() : result.FromResult(404);
    }

    private static async Task<IResult> Heartbeat(
        [FromBody] HeartbeatBody? body, HeartbeatDeviceHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryDeviceId(user, out var deviceId))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, body?.ProbeJson, body?.WorkstationJson)), ct);
        return result.Success ? Results.Ok(result.Value) : ProblemResults.Unauthorized();
    }

    private static async Task<IResult> ClaimCommand(
        ClaimNextCommandHandler handler, ClaimsPrincipal user, int? wait, CancellationToken ct)
    {
        if (!TryDeviceId(user, out var deviceId))
            return ProblemResults.Unauthorized();
        var seconds = wait is > 0 and <= 30 ? wait.Value : 0;
        var result = await handler.HandleAsync(
            new ClaimNextCommandCommand(new ClaimNextCommandRequest(deviceId, TimeSpan.FromSeconds(seconds))), ct);
        return result.Value is null ? Results.NoContent() : Results.Ok(result.Value);
    }

    private static async Task<IResult> CompleteCommand(
        Guid id, [FromBody] CompleteBody body, CompleteCommandHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryDeviceId(user, out var deviceId))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(
            new CompleteCommandCommand(new CompleteCommandRequest(id, deviceId, body.Success, body.ResultJson, body.Error)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> EnqueueCommand(
        Guid id, [FromBody] EnqueueBody body, EnqueueCommandHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId) || IsDevice(user))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(
            new EnqueueCommandCommand(new EnqueueCommandRequest(id, userId, body.Kind, body.PayloadJson, body.ProjectId)), ct);
        if (!result.Success)
        {
            return result.FromResult();
        }
        return Results.Accepted($"/v1/commands/{result.Value!.Id}", result.Value);
    }

    private static async Task<IResult> GetCommand(
        Guid id, GetCommandHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new GetCommandCommand(new GetCommandRequest(id, userId)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> ListRuntimes(
        Guid projectId, ListRuntimesHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId) || IsDevice(user))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new ListRuntimesCommand(new ListRuntimesRequest(projectId, userId)), ct);
        return result.Success ? Results.Ok(result.Value) : result.FromResult(404);
    }

    private static async Task<IResult> AttachRuntime(
        Guid projectId, [FromBody] AttachRuntimeBody body, AttachRuntimeHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId) || IsDevice(user))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(
            new AttachRuntimeCommand(new AttachRuntimeRequest(projectId, body.DeviceId, userId, body.LocalRoot)), ct);
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> DetachRuntime(
        Guid projectId, Guid id, DetachRuntimeHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryUserId(user, out var userId) || IsDevice(user))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new DetachRuntimeCommand(new DetachRuntimeRequest(id, userId)), ct);
        return result.Success ? Results.NoContent() : result.FromResult(404);
    }

    private static async Task<IResult> GetOpenCodeConnections(
        DeviceOpenCodeConnectionsHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryDeviceId(user, out var deviceId))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new DeviceOpenCodeConnectionsCommand(deviceId), ct);
        return result.Success ? Results.Ok(result.Value) : ProblemResults.Unauthorized();
    }

    private static async Task<IResult> GetLlamaSwapConfig(
        GetLlamaSwapConfigHandler handler, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!TryDeviceId(user, out var deviceId))
            return ProblemResults.Unauthorized();
        var result = await handler.HandleAsync(new GetLlamaSwapConfigCommand(new GetLlamaSwapConfigRequest(deviceId)), ct);
        return result.Success ? Results.Ok(result.Value) : ProblemResults.Unauthorized();
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

    private static bool IsDevice(ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType == "DeviceToken";
}
