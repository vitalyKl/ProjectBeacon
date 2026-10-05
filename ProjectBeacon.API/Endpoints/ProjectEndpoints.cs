namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;
using ProjectBeacon.API.Auth;

using Application.Authorization;
using Application.Common;
using Application.Identity;
using Application.Projects;
using Domain.Enums;
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
        app.MapPost("/v1/projects", CreateProject).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPut("/v1/projects/{projectId:guid}", UpdateProject).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}", GetProject).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/orgs/{orgId:guid}/projects", ListProjects).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects", ListAllProjects).RequireAuthorization().DisableAntiforgery().RequireHuman();

        app.MapPost("/v1/projects/{projectId:guid}/members", AddMember).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/projects/{projectId:guid}/members/{userId:guid}", RemoveMember).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/members", ListMembers).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapPost("/v1/projects/{projectId:guid}/invites", CreateInvite).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/invites", ListInvites).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/projects/{projectId:guid}/invites/{inviteId:guid}", RevokeInvite).RequireAuthorization().DisableAntiforgery().RequireHuman();

        app.MapPost("/v1/projects/{projectId:guid}/tokens", CreateToken).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/tokens", ListTokens).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/projects/{projectId:guid}/tokens/{tokenId:guid}", GetToken).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/projects/{projectId:guid}/tokens/{tokenId:guid}", RevokeToken).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapDelete("/v1/tokens/{tokenId:guid}", async (Guid tokenId, RevokeApiTokenHandler handler, HttpContext ctx) => await RevokeToken(Guid.Empty, tokenId, handler, ctx)).RequireAuthorization().DisableAntiforgery().RequireHuman();
        app.MapGet("/v1/tokens/{tokenId:guid}", async (Guid tokenId, GetApiTokenHandler handler, HttpContext ctx) => await GetToken(Guid.Empty, tokenId, handler, ctx)).RequireAuthorization().DisableAntiforgery().RequireHuman();

        return app;
    }

    private static async Task<IResult> CreateProject([FromBody] CreateProjectRequest request, HttpContext ctx, CreateProjectHandler handler, BeaconDbContext db)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new CreateProjectCommand(request, actor));

        return result.Success
            ? Results.Ok(MapProjectResponse(result.Value!))
            : result.FromResult();
    }

    private static async Task<IResult> UpdateProject(Guid projectId, [FromBody] UpdateProjectRequest request, UpdateProjectHandler handler, HttpContext ctx)
    {
        if (projectId != request.ProjectId)
            return ProblemResults.Bad("ProjectId mismatch.");

        var actor = ctx.GetActor();

        var result = await handler.HandleAsync(new UpdateProjectCommand(request, actor));

        return result.Success
            ? Results.Ok(MapProjectResponse(result.Value!))
            : result.FromResult(404);
    }

    private static async Task<IResult> GetProject(Guid projectId, GetProjectHandler handler)
    {
        var result = await handler.HandleAsync(new GetProjectCommand(new GetProjectRequest(projectId)));

        return result.Success
            ? Results.Ok(MapProjectResponse(result.Value!))
            : result.FromResult(404);
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

    private static async Task<IResult> AddMember(Guid projectId, [FromBody] AddProjectMemberRequest request, AddProjectMemberHandler handler, HttpContext ctx)
    {
        if (projectId != request.ProjectId)
            return ProblemResults.Bad("ProjectId mismatch.");
        var actor = ctx.GetActor();

        var result = await handler.HandleAsync(new AddProjectMemberCommand(request, actor));

        return result.Success
            ? Results.Ok(MapMemberResponse(result.Value!))
            : result.FromResult();
    }

    private static async Task<IResult> RemoveMember(Guid projectId, Guid userId, RemoveProjectMemberHandler handler, HttpContext ctx)
    {
        var actor = ctx.GetActor();

        var result = await handler.HandleAsync(new RemoveProjectMemberCommand(new RemoveProjectMemberRequest(projectId, userId), actor));

        return result.Success
            ? Results.NoContent()
            : result.FromResult(404);
    }

    private static async Task<IResult> ListMembers(Guid projectId, GetProjectMembersHandler handler)
    {
        var result = await handler.HandleAsync(new GetProjectMembersCommand(new GetProjectMembersRequest(projectId)));

        return Results.Ok(result.Value?.Select(MapMemberResponse));
    }

    private static ProjectMemberDto MapMemberResponse(ProjectMemberDto dto) =>
        new(dto.Id, dto.UserId, dto.Role, dto.JoinedAt, dto.Login, dto.Email);

    private static async Task<IResult> CreateToken(Guid projectId, [FromBody] CreateApiTokenRequest request, HttpContext ctx, CreateApiTokenHandler handler)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new CreateApiTokenCommand(
            new CreateApiTokenRequest(projectId, request.Name, request.Capabilities, request.ExpiresAt),
            actor));

        return result.Success
            ? Results.Ok(MapTokenResponse(result.Value!))
            : result.FromResult();
    }

    private static async Task<IResult> RevokeToken(Guid projectId, Guid tokenId, RevokeApiTokenHandler handler, HttpContext ctx)
    {
        var actor = ctx.GetActor();
        var resolvedProject = projectId == Guid.Empty ? Guid.Empty : projectId;
        var result = await handler.HandleAsync(new RevokeApiTokenCommand(
            new RevokeApiTokenRequest(resolvedProject, tokenId), actor));

        return result.Success
            ? Results.NoContent()
            : result.FromResult(404);
    }

    private static async Task<IResult> GetToken(Guid projectId, Guid tokenId, GetApiTokenHandler handler, HttpContext ctx)
    {
        var actor = ctx.GetActor();

        var result = await handler.HandleAsync(new GetApiTokenCommand(
            new GetApiTokenRequest(projectId, tokenId), actor));

        return result.Success
            ? Results.Ok(MapTokenResponse(result.Value!))
            : result.FromResult(404);
    }

    private static async Task<IResult> ListTokens(Guid projectId, ListApiTokensHandler handler, HttpContext ctx)
    {
        var actor = ctx.GetActor();

        var result = await handler.HandleAsync(new ListApiTokensCommand(
            new ListApiTokensRequest(projectId), actor));
        if (!result.Success)
            return result.FromResult(404);

        return Results.Ok(result.Value?.Select(MapTokenResponse));
    }

    private static ApiTokenDto MapTokenResponse(ApiTokenDto dto) =>
        new(dto.Id, dto.Name, dto.TokenPrefix, dto.ProjectId, dto.Capabilities, dto.ExpiresAt, dto.LastUsedAt, dto.CreatedAt, dto.Token);

    private static async Task<IResult> CreateInvite(
        Guid projectId, [FromBody] CreateInviteBody body, CreateProjectInviteHandler handler, HttpContext ctx)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new CreateProjectInviteRequest(
            projectId, body.Email, body.Role, actor));
        if (result.Success)
            return Results.Ok(result.Value);
        return result.FromResult();
    }

    private static async Task<IResult> ListInvites(Guid projectId, ListProjectInvitesHandler handler, HttpContext ctx)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new ListInvitesRequest(projectId, actor));
        return result.Success
            ? Results.Ok(result.Value)
            : result.FromResult();
    }

    private static async Task<IResult> RevokeInvite(
        Guid projectId, Guid inviteId, RevokeProjectInviteHandler handler, HttpContext ctx)
    {
        var actor = ctx.GetActor();
        var result = await handler.HandleAsync(new RevokeInviteRequest(inviteId, actor));
        return result.Success
            ? Results.NoContent()
            : result.FromResult(404);
    }

    public record CreateInviteBody(string Email, MemberRole Role);
}
