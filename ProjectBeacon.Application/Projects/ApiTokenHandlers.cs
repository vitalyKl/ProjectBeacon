namespace ProjectBeacon.Application.Projects;

using Application.Common;
using Application.Projects;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

public class CreateApiTokenHandler : ICommandHandler<CreateApiTokenCommand, Result<ApiTokenDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly IConfiguration _config;

    public CreateApiTokenHandler(IDbContextFactory<BeaconDbContext> dbFactory, IConfiguration config)
    {
        _dbFactory = dbFactory;
        _config = config;
    }

    public async Task<Result<ApiTokenDto>> HandleAsync(CreateApiTokenCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
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
            tokenValue,
            token.ProjectId,
            token.Capabilities,
            token.ExpiresAt,
            token.LastUsedAt,
            token.CreatedAt));
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
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public RevokeApiTokenHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(RevokeApiTokenCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var token = await db.ApiTokens.FindAsync([command.Request.TokenId], ct);
        if (token is null)
            return Result.Failure("API token not found.");

        db.ApiTokens.Remove(token);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}

public class GetApiTokenHandler : ICommandHandler<GetApiTokenCommand, Result<ApiTokenDto>>
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetApiTokenHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<ApiTokenDto>> HandleAsync(GetApiTokenCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var token = await db.ApiTokens.FindAsync([command.Request.TokenId], ct);
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
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListApiTokensHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<ApiTokenDto>>> HandleAsync(ListApiTokensCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
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
                t.CreatedAt))
            .ToListAsync(ct);
        return Result.Ok((IList<ApiTokenDto>)tokens);
    }
}