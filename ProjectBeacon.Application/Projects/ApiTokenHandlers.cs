namespace ProjectBeacon.Application.Projects;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

public class CreateApiTokenHandler : ICommandHandler<CreateApiTokenCommand, Result<ApiTokenDto>>
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IConfiguration _config;

    public CreateApiTokenHandler(IBeaconDbFactory dbFactory, IConfiguration config)
    {
        _dbFactory = dbFactory;
        _config = config;
    }

    public async Task<Result<ApiTokenDto>> HandleAsync(CreateApiTokenCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();

        var actor = new ActorContext(command.ActorUserId, command.ActorIsAdmin, command.ActorIsApiToken);
        var auth = await ProjectAuthorization.CreateToken(
            db, command.Request.ProjectId, actor,
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
            command.Request.CreatedByUserId);

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

public class RevokeApiTokenHandler : ICommandHandler<RevokeApiTokenCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public RevokeApiTokenHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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

        var actor = new ActorContext(command.ActorUserId, command.ActorIsAdmin, command.ActorIsApiToken);
        var auth = await ProjectAuthorization.RevokeToken(
            db, projectId, actor,
            command.Request.TokenId, ct);
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

public class GetApiTokenHandler : ICommandHandler<GetApiTokenCommand, Result<ApiTokenDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetApiTokenHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

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

        var actor = new ActorContext(command.ActorUserId, command.ActorIsAdmin, command.ActorIsApiToken);
        if (!await ProjectAuthorization.CanManageProjectAsync(db, projectId, actor, ct))
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

public class ListApiTokensHandler : ICommandHandler<ListApiTokensCommand, Result<IList<ApiTokenDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListApiTokensHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ApiTokenDto>>> HandleAsync(ListApiTokensCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();

        var actor = new ActorContext(command.ActorUserId, command.ActorIsAdmin, command.ActorIsApiToken);
        if (!await ProjectAuthorization.CanManageProjectAsync(db, command.Request.ProjectId, actor, ct))
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
