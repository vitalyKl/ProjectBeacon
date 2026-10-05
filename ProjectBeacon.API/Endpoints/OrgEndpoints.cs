namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

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
        app.MapPost("/v1/orgs", CreateOrg).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPut("/v1/orgs/{orgId:guid}", UpdateOrg).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/orgs/{orgId:guid}", GetOrg).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/orgs", ListOrgs).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/orgs/{orgId:guid}/invites", CreateInvite).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/orgs/{orgId:guid}/invites", ListInvites).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/orgs/{orgId:guid}/invites/{inviteId:guid}", RevokeInvite).RequireAuthorization().DisableAntiforgery().RequireHuman();

        return app;
    }

    private static async Task<IResult> CreateOrg([FromBody] CreateOrgRequest request, HttpContext ctx, CreateOrgHandler handler)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(command: new CreateOrgCommand(request, actor));

        return result.Success
            ? Results.Ok(MapOrgResponse(result.Value!))
            : result.FromResult();
    }

    private static async Task<IResult> UpdateOrg(Guid orgId, [FromBody] UpdateOrgRequest request, UpdateOrgHandler handler)
    {
        if (orgId != request.OrgId)
            return ProblemResults.Bad("OrgId mismatch.");

        var result = await handler.HandleAsync(new UpdateOrgCommand(request));

        return result.Success
            ? Results.Ok(MapOrgResponse(result.Value!))
            : result.FromResult(404);
    }

    private static async Task<IResult> GetOrg(Guid orgId, GetOrgHandler handler)
    {
        var result = await handler.HandleAsync(new GetOrgCommand(new GetOrgRequest(orgId)));

        return result.Success
            ? Results.Ok(MapOrgResponse(result.Value!))
            : result.FromResult(404);
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
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new CreateOrgInviteRequest(
            orgId, body.Email, body.Role, actor));
        if (result.Success)
            return Results.Ok(result.Value);
        return result.FromResult();
    }

    private static async Task<IResult> ListInvites(Guid orgId, ListOrgInvitesHandler handler, HttpContext ctx)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new ListInvitesRequest(orgId, actor));
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> RevokeInvite(Guid orgId, Guid inviteId, RevokeOrgInviteHandler handler, HttpContext ctx)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new RevokeInviteRequest(inviteId, actor));
        return result.Success
            ? Results.NoContent()
            : result.FromResult(404);
    }

    public record CreateInviteBody(string Email, MemberRole Role);
}
