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
    private readonly BeaconDbContext _db;
    private readonly IConfiguration _config;

    public CreateApiTokenHandler(BeaconDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<Result<ApiTokenDto>> HandleAsync(CreateApiTokenCommand command, CancellationToken ct = default)
    {
        var project = await _db.Projects.FindAsync([command.Request.ProjectId], ct);
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

        _db.ApiTokens.Add(token);
        await _db.SaveChangesAsync(ct);

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

public class RevokeApiTokenHandler : ICommandHandler<RevokeApiTokenCommand, Result<bool>>
{
    private readonly BeaconDbContext _db;

    public RevokeApiTokenHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<bool>> HandleAsync(RevokeApiTokenCommand command, CancellationToken ct = default)
    {
        var token = await _db.ApiTokens.FindAsync([command.Request.TokenId], ct);
        if (token is null)
            return Result.Failure<bool>("API token not found.");

        _db.ApiTokens.Remove(token);
        await _db.SaveChangesAsync(ct);

        return Result.Ok(true);
    }
}

public class GetApiTokenHandler : ICommandHandler<GetApiTokenCommand, Result<ApiTokenDto>>
{
    private readonly BeaconDbContext _db;

    public GetApiTokenHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<ApiTokenDto>> HandleAsync(GetApiTokenCommand command, CancellationToken ct = default)
    {
        var token = await _db.ApiTokens.FindAsync([command.Request.TokenId], ct);
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
    private readonly BeaconDbContext _db;

    public ListApiTokensHandler(BeaconDbContext db) => _db = db;

    public async Task<Result<IList<ApiTokenDto>>> HandleAsync(ListApiTokensCommand command, CancellationToken ct = default)
    {
        var tokens = await _db.ApiTokens
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
