namespace ProjectBeacon.Application.Projects;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
/// <summary>
/// Mints a project API token and returns the raw value once. The caller must be allowed to administer the project.
/// </summary>
public class CreateApiTokenHandler : ICommandHandler<CreateApiTokenCommand, Result<ApiTokenDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IConfiguration _config;
    private readonly IAuthorizationService _auth;

    public CreateApiTokenHandler(IBeaconDbFactory dbFactory, IConfiguration config, IAuthorizationService auth)
    {
        _dbFactory = dbFactory;
        _config = config;
        _auth = auth;
    }

    public async Task<Result<ApiTokenDto>> HandleAsync(CreateApiTokenCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();

        var auth = await _auth.CanCreateTokenAsync(
            db, command.Actor, command.Request.ProjectId,
            command.Request.Capabilities, ct);
        if (!auth.Success)
            return Result.Failure<ApiTokenDto>(auth);

        var project = await db.Projects.FindAsync([command.Request.ProjectId], ct);
        if (project is null)
            return Result.Failure<ApiTokenDto>("Project not found.");

        var tokenValue = GenerateToken();
        var tokenHash = ComputeHash(tokenValue);
        var prefix = tokenValue[..Math.Min(8, tokenValue.Length)];

        var token = ApiToken.Create(
            command.Request.Name,
            tokenHash,
            prefix,
            command.Request.ProjectId,
            command.Request.Capabilities,
            command.Request.ExpiresAt,
            command.Actor.UserId);

        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new ApiTokenDto(
            token.Id,
            token.Name,
            prefix,
            token.ProjectId,
            token.Capabilities,
            token.ExpiresAt,
            token.LastUsedAt,
            token.CreatedAt,
            tokenValue));
    }

    private static string GenerateToken()
    {
        var random = System.Security.Cryptography.RandomNumberGenerator.GetBytes(24);
        return "bcn_" + Convert.ToHexString(random).ToLowerInvariant();
    }

    private static string ComputeHash(string token)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }
}
/// <summary>
/// Deletes a project API token. A token outside the caller's project is not found.
/// </summary>
public class RevokeApiTokenHandler : ICommandHandler<RevokeApiTokenCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;

    public RevokeApiTokenHandler(IBeaconDbFactory dbFactory, IAuthorizationService auth)
    {
        _dbFactory = dbFactory;
        _auth = auth;
    }

    public async Task<Result> HandleAsync(RevokeApiTokenCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();

        var projectId = command.Request.ProjectId;
        if (projectId == Guid.Empty)
        {
            var existing = await db.ApiTokens.IgnoreQueryFilters()
                .Where(t => t.Id == command.Request.TokenId)
                .Select(t => t.ProjectId)
                .FirstOrDefaultAsync(ct);
            if (existing == Guid.Empty)
                return Result.Failure("API token not found.");
            projectId = existing;
        }

        var auth = await _auth.CanAsync(db, command.Actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
        if (!auth.Success)
            return Result.Failure(auth);

        var token = await db.ApiTokens
            .FirstOrDefaultAsync(t => t.Id == command.Request.TokenId && t.ProjectId == projectId, ct);
        if (token is null)
            return Result.Failure("API token not found.");

        db.ApiTokens.Remove(token);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
/// <summary>
/// Loads one API token without the raw secret. Forbidden when the caller cannot administer the project.
/// </summary>
public class GetApiTokenHandler : ICommandHandler<GetApiTokenCommand, Result<ApiTokenDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;

    public GetApiTokenHandler(IBeaconDbFactory dbFactory, IAuthorizationService auth)
    {
        _dbFactory = dbFactory;
        _auth = auth;
    }

    public async Task<Result<ApiTokenDto>> HandleAsync(GetApiTokenCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();

        var tokenId = command.Request.TokenId;
        var projectId = command.Request.ProjectId;
        if (projectId == Guid.Empty)
        {
            var owner = await db.ApiTokens.IgnoreQueryFilters()
                .Where(t => t.Id == tokenId)
                .Select(t => t.ProjectId)
                .FirstOrDefaultAsync(ct);
            if (owner == Guid.Empty)
                return Result.Failure<ApiTokenDto>("API token not found.");
            projectId = owner;
        }

        var check = await _auth.CanAsync(db, command.Actor, ResourceType.Project, AuthAction.Administer, projectId: projectId, ct: ct);
        if (!check.Success)
            return Result.Forbidden<ApiTokenDto>();

        var token = await db.ApiTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.ProjectId == projectId, ct);
        if (token is null)
            return Result.Failure<ApiTokenDto>("API token not found.");

        return Result.Ok(new ApiTokenDto(
            token.Id,
            token.Name,
            token.TokenPrefix,
            token.ProjectId,
            token.Capabilities,
            token.ExpiresAt,
            token.LastUsedAt,
            token.CreatedAt));
    }
}
/// <summary>
/// Lists project API tokens. Forbidden when the caller cannot administer the project.
/// </summary>
public class ListApiTokensHandler : ICommandHandler<ListApiTokensCommand, Result<IList<ApiTokenDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;

    public ListApiTokensHandler(IBeaconDbFactory dbFactory, IAuthorizationService auth)
    {
        _dbFactory = dbFactory;
        _auth = auth;
    }

    public async Task<Result<IList<ApiTokenDto>>> HandleAsync(ListApiTokensCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();

        var check = await _auth.CanAsync(db, command.Actor, ResourceType.Project, AuthAction.Administer, projectId: command.Request.ProjectId, ct: ct);
        if (!check.Success)
            return Result.Forbidden<IList<ApiTokenDto>>();

        var tokens = await db.ApiTokens
            .Where(t => t.ProjectId == command.Request.ProjectId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new ApiTokenDto(
                t.Id,
                t.Name,
                t.TokenPrefix,
                t.ProjectId,
                t.Capabilities,
                t.ExpiresAt,
                t.LastUsedAt,
                t.CreatedAt,
                null))
            .ToListAsync(ct);
        return Result.Ok((IList<ApiTokenDto>)tokens);
    }
}
