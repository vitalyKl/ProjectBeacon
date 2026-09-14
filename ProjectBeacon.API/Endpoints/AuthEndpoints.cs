namespace ProjectBeacon.API.Endpoints;

using Application.Auth;
using Application.Common;
using Application.Projects;
using Infrastructure.Data;
using Domain.Entities.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/auth/bootstrap", Bootstrap).AllowAnonymous().DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/v1/auth/recover-admin", RecoverAdmin).AllowAnonymous().DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/v1/auth/login", Login).AllowAnonymous().DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/v1/auth/register", Register).AllowAnonymous().DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/v1/auth/logout", Logout).AllowAnonymous().DisableAntiforgery().RequireRateLimiting("auth");
        app.MapGet("/v1/auth/me", GetMe).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Bootstrap([FromBody] BootstrapRequest? request, BootstrapHandler handler, HttpContext ctx)
    {
        var authHeader = ctx.Request.Headers["Authorization"].ToString();
        var providedToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader.Substring(7).Trim()
            : string.Empty;

        var result = await handler.HandleAsync(providedToken);

        if (result.Success)
            return Results.Ok(new
            {
                result.Value.UserId,
                result.Value.Login,
                result.Value.Email,
                result.Value.IsAdmin,
                result.Value.Password
            });

        var statusCode = result.Error == "Bootstrap already completed."
            ? 409
            : 401;
        return Results.Json(new { error = result.Error }, statusCode: statusCode);
    }

    private static async Task<IResult> RecoverAdmin(RecoverAdminHandler handler, HttpContext ctx)
    {
        var authHeader = ctx.Request.Headers["Authorization"].ToString();
        var providedToken = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader.Substring(7).Trim()
            : string.Empty;

        var result = await handler.HandleAsync(providedToken);

        if (result.Success)
            return Results.Ok(new
            {
                result.Value.UserId,
                result.Value.Login,
                result.Value.Email,
                result.Value.IsAdmin,
                result.Value.Password
            });

        var statusCode = result.Error is "Invalid bootstrap token." or "Bootstrap token is not configured."
            ? 401
            : 400;
        return Results.Json(new { error = result.Error }, statusCode: statusCode);
    }

    private static async Task<IResult> Login([FromBody] LoginRequest request, LoginHandler handler)
    {
        var result = await handler.HandleAsync(new LoginCommand(request));

        return result.Success
            ? Results.Ok(new
            {
                result.Value.UserId,
                result.Value.Login,
                result.Value.Email,
                result.Value.IsAdmin,
                result.Value.Token
            })
            : Results.Json(new { error = result.Error }, statusCode: 401);
    }

    private static async Task<IResult> Register([FromBody] RegisterRequest request, RegisterHandler handler)
    {
        var result = await handler.HandleAsync(new RegisterCommand(request));

        return result.Success
            ? Results.Ok(new
            {
                result.Value.UserId,
                result.Value.Login,
                result.Value.Email
            })
            : Results.Json(new { error = result.Error }, statusCode: 400);
    }

    private static async Task<IResult> Logout([FromBody] LogoutRequest request, BeaconDbContext db)
    {
        await db.Sessions
            .Where(s => s.UserId == request.UserId && s.IsActive)
            .ExecuteDeleteAsync();

        return Results.Ok();
    }

    private static async Task<IResult> GetMe(BeaconDbContext db, HttpContext ctx)
    {
        var userIdStr = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            return Results.Unauthorized();

        var user = await db.Users.FindAsync([userId]);
        if (user is null)
            return Results.Unauthorized();

        return Results.Ok(new
        {
            user.Id,
            user.Login,
            user.Email,
            user.IsAdmin,
            user.LastLoginAt
        });
    }
}
