namespace ProjectBeacon.API.Endpoints;

using Application.Common;
using Application.Identity;
using Application.Projects;
using Infrastructure.Data;
using Domain.Entities.Identity;
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

        return app;
    }

    private static async Task<IResult> CreateOrg([FromBody] CreateOrgRequest request, HttpContext ctx, CreateOrgHandler handler)
    {
        var result = await handler.HandleAsync(command: new CreateOrgCommand(request));

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
}
