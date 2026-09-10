namespace ProjectBeacon.API.Endpoints;

using System.Security.Claims;
using Application.Common;
using Application.Projects;
using Infrastructure.Data;
using Domain.Entities.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/projects", CreateProject).RequireAuthorization().DisableAntiforgery();
        app.MapPut("/v1/projects/{projectId:guid}", UpdateProject).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}", GetProject).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/orgs/{orgId:guid}/projects", ListProjects).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects", ListAllProjects).RequireAuthorization().DisableAntiforgery();

        app.MapPost("/v1/projects/{projectId:guid}/members", AddMember).RequireAuthorization().DisableAntiforgery();
        app.MapDelete("/v1/projects/{projectId:guid}/members/{userId:guid}", RemoveMember).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}/members", ListMembers).RequireAuthorization().DisableAntiforgery();

        app.MapPost("/v1/projects/{projectId:guid}/tokens", CreateToken).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}/tokens", ListTokens).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/projects/{projectId:guid}/tokens/{tokenId:guid}", GetToken).RequireAuthorization().DisableAntiforgery();
        app.MapDelete("/v1/projects/{projectId:guid}/tokens/{tokenId:guid}", RevokeToken).RequireAuthorization().DisableAntiforgery();
        app.MapDelete("/v1/tokens/{tokenId:guid}", RevokeToken).RequireAuthorization().DisableAntiforgery();
        app.MapGet("/v1/tokens/{tokenId:guid}", GetToken).RequireAuthorization().DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> CreateProject([FromBody] CreateProjectRequest request, HttpContext ctx, CreateProjectHandler handler, BeaconDbContext db)
    {
        var createdBy = ActorUserId(ctx);
        var result = await handler.HandleAsync(new CreateProjectCommand(request with { CreatedByUserId = createdBy }));

        return result.Success
            ? Results.Ok(MapProjectResponse(result.Value))
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> UpdateProject(Guid projectId, [FromBody] UpdateProjectRequest request, UpdateProjectHandler handler)
    {
        if (projectId != request.ProjectId)
            return Results.BadRequest("ProjectId mismatch.");

        var result = await handler.HandleAsync(new UpdateProjectCommand(request));

        return result.Success
            ? Results.Ok(MapProjectResponse(result.Value))
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> GetProject(Guid projectId, GetProjectHandler handler)
    {
        var result = await handler.HandleAsync(new GetProjectCommand(new GetProjectRequest(projectId)));

        return result.Success
            ? Results.Ok(MapProjectResponse(result.Value))
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> ListProjects(Guid orgId, ListProjectsHandler handler)
    {
        var result = await handler.HandleAsync(new ListProjectsCommand(new ListProjectsRequest(orgId)));

        return Results.Ok(result.Value?.Select(MapProjectResponse));
    }

    private static async Task<IResult> ListAllProjects(ListProjectsHandler handler)
    {
        var result = await handler.HandleAsync(new ListProjectsCommand(new ListProjectsRequest(null)));

        return Results.Ok(result.Value?.Select(MapProjectResponse));
    }

    private static ProjectDto MapProjectResponse(ProjectDto dto) =>
        new(dto.Id, dto.Name, dto.Description, dto.OrgId, dto.CreatedAt, dto.UpdatedAt);

    private static async Task<IResult> AddMember(Guid projectId, [FromBody] AddProjectMemberRequest request, AddProjectMemberHandler handler)
    {
        if (projectId != request.ProjectId)
            return Results.BadRequest("ProjectId mismatch.");

        var result = await handler.HandleAsync(new AddProjectMemberCommand(request));

        return result.Success
            ? Results.Ok(MapMemberResponse(result.Value))
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> RemoveMember(Guid projectId, Guid userId, RemoveProjectMemberHandler handler)
    {
        var result = await handler.HandleAsync(new RemoveProjectMemberCommand(new RemoveProjectMemberRequest(projectId, userId)));

        return result.Success
            ? Results.NoContent()
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> ListMembers(Guid projectId, GetProjectMembersHandler handler)
    {
        var result = await handler.HandleAsync(new GetProjectMembersCommand(new GetProjectMembersRequest(projectId)));

        return Results.Ok(result.Value?.Select(MapMemberResponse));
    }

    private static ProjectMemberDto MapMemberResponse(ProjectMemberDto dto) =>
        new(dto.Id, dto.UserId, dto.Role, dto.JoinedAt);

    private static async Task<IResult> CreateToken(Guid projectId, [FromBody] CreateApiTokenRequest request, [FromServices] IConfiguration config, CreateApiTokenHandler handler)
    {
        var result = await handler.HandleAsync(new CreateApiTokenCommand(new CreateApiTokenRequest(projectId, request.Name, request.Capabilities, request.ExpiresAt, request.CreatedByUserId)));

        return result.Success
            ? Results.Ok(MapTokenResponse(result.Value))
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> RevokeToken(Guid tokenId, RevokeApiTokenHandler handler)
    {
        var result = await handler.HandleAsync(new RevokeApiTokenCommand(new RevokeApiTokenRequest(tokenId)));

        return result.Success
            ? Results.NoContent()
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> GetToken(Guid tokenId, GetApiTokenHandler handler)
    {
        var result = await handler.HandleAsync(new GetApiTokenCommand(new GetApiTokenRequest(tokenId)));

        return result.Success
            ? Results.Ok(MapTokenResponse(result.Value))
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> ListTokens(Guid projectId, ListApiTokensHandler handler)
    {
        var result = await handler.HandleAsync(new ListApiTokensCommand(new ListApiTokensRequest(projectId)));
        return Results.Ok(result.Value?.Select(MapTokenResponse));
    }

    private static ApiTokenDto MapTokenResponse(ApiTokenDto dto) =>
        new(dto.Id, dto.Name, dto.TokenPrefix, dto.ProjectId, dto.Capabilities, dto.ExpiresAt, dto.LastUsedAt, dto.CreatedAt);

    private static Guid? ActorUserId(HttpContext ctx) =>
        Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
