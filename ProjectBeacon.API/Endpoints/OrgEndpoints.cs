namespace ProjectBeacon.API.Endpoints;

using System.Security.Claims;
using Application.Common;
using Application.Identity;
using Application.Projects;
using Infrastructure.Data;
using Domain.Entities.Identity;
using Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

public static class OrgEndpoints
{
    public static IEndpointRouteBuilder MapOrgEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/orgs", CreateOrg).RequireAuthorization().DisableAntiforgery();
        app.MapPut("/v1/orgs/{orgId:guid}", UpdateOrg).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/orgs/{orgId:guid}", GetOrg).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/orgs", ListOrgs).RequireAuthorization().DisableAntiforgery();
        app.MapPost("/v1/orgs/{orgId:guid}/invites", CreateInvite).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/orgs/{orgId:guid}/invites", ListInvites).RequireAuthorization().DisableAntiforgery();
        app.MapDelete("/v1/orgs/{orgId:guid}/invites/{inviteId:guid}", RevokeInvite).RequireAuthorization().DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> CreateOrg([FromBody] CreateOrgRequest request, HttpContext ctx, CreateOrgHandler handler)
    {
        var createdBy = ActorUserId(ctx);
        var result = await handler.HandleAsync(command: new CreateOrgCommand(request with { CreatedByUserId = createdBy }));

        return result.Success
            ? Results.Ok(MapOrgResponse(result.Value))
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> UpdateOrg(Guid orgId, [FromBody] UpdateOrgRequest request, UpdateOrgHandler handler)
    {
        if (orgId != request.OrgId)
            return Results.BadRequest("OrgId mismatch.");

        var result = await handler.HandleAsync(new UpdateOrgCommand(request));

        return result.Success
            ? Results.Ok(MapOrgResponse(result.Value))
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> GetOrg(Guid orgId, GetOrgHandler handler)
    {
        var result = await handler.HandleAsync(new GetOrgCommand(new GetOrgRequest(orgId)));

        return result.Success
            ? Results.Ok(MapOrgResponse(result.Value))
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> ListOrgs(ListOrgsHandler handler, BeaconDbContext db, HttpContext ctx)
    {
        var result = await handler.HandleAsync(new ListOrgsCommand(new ListOrgsRequest()));

        return Results.Ok(result.Value?.Select(MapOrgResponse));
    }

    private static OrgDto MapOrgResponse(OrgDto dto) =>
        new(dto.Id, dto.Name, dto.Description, dto.CreatedAt, dto.UpdatedAt);

    private static async Task<IResult> CreateInvite(
        Guid orgId, [FromBody] CreateInviteBody body, CreateOrgInviteHandler handler, HttpContext ctx)
    {
        var actor = ActorUserId(ctx);
        if (actor is null)
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new CreateOrgInviteRequest(
            orgId, body.Email, body.Role, actor.Value, ActorIsAdmin(ctx)));
        if (result.Success)
            return Results.Ok(result.Value);
        var status = result.Error == "Forbidden." ? 403 : 400;
        return Results.Json(new { error = result.Error }, statusCode: status);
    }

    private static async Task<IResult> ListInvites(Guid orgId, ListOrgInvitesHandler handler, HttpContext ctx)
    {
        var actor = ActorUserId(ctx);
        if (actor is null)
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new ListInvitesRequest(orgId, actor.Value, ActorIsAdmin(ctx)));
        return result.Success
            ? Results.Ok(result.Value)
            : Results.Json(new { error = result.Error }, statusCode: result.Error == "Forbidden." ? 403 : 400);
    }

    private static async Task<IResult> RevokeInvite(Guid orgId, Guid inviteId, RevokeOrgInviteHandler handler, HttpContext ctx)
    {
        var actor = ActorUserId(ctx);
        if (actor is null)
            return Results.Unauthorized();
        var result = await handler.HandleAsync(new RevokeInviteRequest(inviteId, actor.Value, ActorIsAdmin(ctx)));
        return result.Success
            ? Results.NoContent()
            : Results.Json(new { error = result.Error }, statusCode: result.Error == "Forbidden." ? 403 : 404);
    }

    public record CreateInviteBody(string Email, MemberRole Role);

    private static Guid? ActorUserId(HttpContext ctx) =>
        Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static bool ActorIsAdmin(HttpContext ctx) =>
        bool.TryParse(ctx.User.FindFirstValue("isAdmin"), out var flag) && flag;
}
