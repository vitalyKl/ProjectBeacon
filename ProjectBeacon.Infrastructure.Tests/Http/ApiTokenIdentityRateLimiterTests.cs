namespace ProjectBeacon.Infrastructure.Tests;

using System.Security.Claims;
using Application.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using ProjectBeacon.Infrastructure.Http;

public sealed class ApiTokenIdentityRateLimiterTests
{
    [Fact]
    public async Task TokensCreatedBySameUserShareIdentityBucket()
    {
        using var limiter = new ApiTokenIdentityRateLimiter(
            permitLimit: 2,
            windowSeconds: 60,
            readPermitLimit: 10,
            attachmentUploadPermitLimit: 10);
        var creatorId = Guid.NewGuid();

        using var first = await limiter.AcquireAsync(CreateContext(Guid.NewGuid(), creatorId));
        using var second = await limiter.AcquireAsync(CreateContext(Guid.NewGuid(), creatorId));
        using var third = await limiter.AcquireAsync(CreateContext(Guid.NewGuid(), creatorId));

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(third.IsAcquired);
    }

    [Fact]
    public async Task ServiceTokensWithoutCreatorUseIndependentTokenBuckets()
    {
        using var limiter = new ApiTokenIdentityRateLimiter(
            permitLimit: 1,
            windowSeconds: 60,
            readPermitLimit: 10,
            attachmentUploadPermitLimit: 10);
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();

        using var firstA = await limiter.AcquireAsync(CreateContext(tokenA, creatorId: null));
        using var secondA = await limiter.AcquireAsync(CreateContext(tokenA, creatorId: null));
        using var firstB = await limiter.AcquireAsync(CreateContext(tokenB, creatorId: null));

        Assert.True(firstA.IsAcquired);
        Assert.False(secondA.IsAcquired);
        Assert.True(firstB.IsAcquired);
    }

    [Fact]
    public async Task PolicyBucketsRemainIndependentForSameCreator()
    {
        using var limiter = new ApiTokenIdentityRateLimiter(
            permitLimit: 1,
            windowSeconds: 60,
            readPermitLimit: 1,
            attachmentUploadPermitLimit: 1);
        var creatorId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();

        using var auth = await limiter.AcquireAsync(CreateContext(tokenId, creatorId, "auth"));
        using var read = await limiter.AcquireAsync(CreateContext(tokenId, creatorId, "auth-read"));
        using var upload = await limiter.AcquireAsync(CreateContext(tokenId, creatorId, "attachment-upload"));
        using var authAgain = await limiter.AcquireAsync(CreateContext(tokenId, creatorId, "auth"));

        Assert.True(auth.IsAcquired);
        Assert.True(read.IsAcquired);
        Assert.True(upload.IsAcquired);
        Assert.False(authAgain.IsAcquired);
    }

    private static DefaultHttpContext CreateContext(
        Guid tokenId,
        Guid? creatorId,
        string policyName = "auth")
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute(policyName)),
            "API token rate-limit test"));

        var claims = new List<Claim>
        {
            new("token_id", tokenId.ToString("D"))
        };
        if (creatorId is { } userId)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.ToString("D")));

        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            ActorContextFactory.ApiTokenAuthType));
        return context;
    }
}
