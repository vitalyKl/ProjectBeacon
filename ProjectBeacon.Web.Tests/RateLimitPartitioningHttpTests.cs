namespace ProjectBeacon.Web.Tests;

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectBeacon.Infrastructure.Http;
using ProjectBeacon.Application.Authorization;

/// <summary>
/// Verifies that opted-in endpoints use forwarded client IPs and authenticated user IDs
/// as independent rate-limit partition keys. The pipeline mirrors the production order:
/// trusted forwarded headers, routing, authentication, then the rate limiter.
/// </summary>
public sealed class RateLimitPartitioningHttpTests
{
    private const string ProxyIp = "203.0.113.1";
    private const string ClientA = "203.0.113.10";
    private const string ClientB = "203.0.113.20";
    private const string UserA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string UserB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public async Task SameClientIpBehindProxy_ShareOneBucket()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, ClientA)).StatusCode);

        var blocked = await PostAsync(client, ClientA);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Contains("application/problem+json", blocked.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task DistinctClientIpsBehindProxy_GetSeparateBuckets()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, ClientA)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(client, ClientA)).StatusCode);

        // B has not consumed A's bucket.
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, ClientB)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(client, ClientB)).StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUserSharesBucketAcrossDifferentClientIps()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, ClientA, UserA)).StatusCode);

        // Client B has a fresh IP bucket, but the same authenticated user is already at its limit.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(client, ClientB, UserA)).StatusCode);

        // A different authenticated user has an independent identity bucket.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, ClientB, UserB)).StatusCode);
    }

    [Fact]
    public async Task AuthAndReadPoliciesHaveIndependentUserBuckets()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, ClientA, UserA)).StatusCode);

        // The same user has a separate auth-read bucket and must not inherit auth exhaustion.
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, ClientA, UserA)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(client, ClientA, UserA)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsync(client, ClientA, UserA)).StatusCode);
    }

    [Fact]
    public async Task AttachmentUploadLimitIsPerAuthenticatedUserAcrossClientIps()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        // Consume the upload allowance for UserA through distinct IPs so none of the
        // per-IP buckets is exhausted while the per-user bucket is being tested.
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, $"203.0.113.{30 + i}", UserA)).StatusCode);

        // Changing the client IP does not reset UserA's authenticated upload allowance.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await UploadAsync(client, ClientB, UserA)).StatusCode);

        // UserB on that same IP has a fresh user bucket and the IP bucket still has capacity.
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, ClientB, UserB)).StatusCode);
    }

    [Fact]
    public async Task ApiTokenCreatorSharesBucketAcrossDifferentTokensAndClientIps()
    {
        await using var app = await BuildApiTokenAppAsync();
        using var client = app.GetTestClient();

        // Each request gets a fresh IP and token id. The verified token creator is the
        // stable account identity, so a second token cannot reset the creator's allowance.
        for (var i = 0; i < 5; i++)
        {
            var response = await PostApiTokenAsync(
                client,
                $"198.51.100.{i + 1}",
                UserA,
                Guid.NewGuid().ToString("D"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var blocked = await PostApiTokenAsync(
            client,
            "198.51.100.20",
            UserA,
            Guid.NewGuid().ToString("D"));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // A different creator remains independent even though the request uses the same route.
        var otherCreator = await PostApiTokenAsync(
            client,
            "198.51.100.21",
            UserB,
            Guid.NewGuid().ToString("D"));
        Assert.Equal(HttpStatusCode.OK, otherCreator.StatusCode);
    }

    [Fact]
    public async Task UntrustedPeerCannotSpoofForwardedClientIp()
    {
        const string untrustedPeer = "198.51.100.99";
        await using var app = await BuildAppAsync(untrustedPeer);
        using var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"198.51.100.{i + 1}")).StatusCode);

        // All spoofed X-Forwarded-For values are ignored because the peer is not trusted.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(client, "198.51.100.200")).StatusCode);
    }

    [Fact]
    public async Task ExplicitTrustListReplacesDefaultLoopbackTrust()
    {
        await using var app = await BuildAppAsync("127.0.0.1");
        using var client = app.GetTestClient();

        for (var i = 1; i <= 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"192.0.2.{i}")).StatusCode);

        // Loopback is not trusted once an explicit proxy/network allowlist is configured.
        // All forwarded values are ignored and continue consuming the loopback IP bucket.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(client, "192.0.2.200")).StatusCode);
    }

    [Fact]
    public async Task NoForwardedFor_FallsBackToProxyRemoteIp()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(client, null)).StatusCode);
    }

    private static async Task<WebApplication> BuildApiTokenAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration["ForwardedHeaders:KnownNetworks:0"] = $"{ProxyIp}/32";
        builder.Services.AddRateLimiter(options => AuthRateLimiter.Configure(options, 5, 60, 5));
        builder.Services.AddSingleton(new ApiTokenIdentityRateLimiter(5, 60, 5));

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(ProxyIp);
            await next(context);
        });
        app.UseForwardedHeaders(ForwardedHeadersOptionsFactory.Create(builder.Configuration));
        app.UseRouting();

        // The built-in limiter runs before credential verification and provides the IP gate.
        app.UseRateLimiter();

        // Simulate ApiTokenAuthMiddleware after it has validated bcn_ and resolved token claims.
        app.Use(async (context, next) =>
        {
            var creator = context.Request.Headers["X-Test-User"].ToString();
            var token = context.Request.Headers["X-Test-Token"].ToString();
            var identity = new ClaimsIdentity(ActorContextFactory.ApiTokenAuthType);
            identity.AddClaim(new Claim("token_id", token));
            if (Guid.TryParse(creator, out var creatorId))
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, creatorId.ToString("D")));
            context.User = new ClaimsPrincipal(identity);
            await next(context);
        });
        app.UseMiddleware<ApiTokenIdentityRateLimitMiddleware>();
        app.MapPost("/auth-test", () => Results.Ok()).RequireRateLimiting("auth");

        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> PostApiTokenAsync(
        HttpClient client,
        string forwardedFor,
        string creatorId,
        string tokenId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth-test");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Headers.Add("X-Test-User", creatorId);
        request.Headers.Add("X-Test-Token", tokenId);
        return await client.SendAsync(request);
    }

    private static async Task<WebApplication> BuildAppAsync(
        string remoteIp = ProxyIp,
        int attachmentUploadPermitLimit = 10)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration["ForwardedHeaders:KnownNetworks:0"] = $"{ProxyIp}/32";
        builder.Services.AddRateLimiter(options => AuthRateLimiter.Configure(
            options, 5, 60, 5, attachmentUploadPermitLimit));

        var app = builder.Build();

        // TestServer supplies no socket remote endpoint; simulate the proxy hop.
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            await next(context);
        });
        app.UseForwardedHeaders(ForwardedHeadersOptionsFactory.Create(builder.Configuration));
        app.UseRouting();

        // Test authentication middleware: production calls UseAuthentication at this point.
        app.Use(async (context, next) =>
        {
            var subject = context.Request.Headers["X-Test-User"].ToString();
            if (Guid.TryParse(subject, out var userId) && userId != Guid.Empty)
            {
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString("D"))],
                    authenticationType: "test");
                context.User = new ClaimsPrincipal(identity);
            }

            await next(context);
        });
        app.UseRateLimiter();
        app.MapPost("/auth-test", () => Results.Ok()).RequireRateLimiting("auth");
        app.MapGet("/auth-read-test", () => Results.Ok()).RequireRateLimiting("auth-read");
        app.MapPut("/attachment-upload-test", () => Results.Ok()).RequireRateLimiting("attachment-upload");

        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string? forwardedFor,
        string? userId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/attachment-upload-test");
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (userId is not null)
            request.Headers.Add("X-Test-User", userId);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string? forwardedFor,
        string? userId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth-read-test");
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (userId is not null)
            request.Headers.Add("X-Test-User", userId);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string? forwardedFor,
        string? userId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth-test");
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (userId is not null)
            request.Headers.Add("X-Test-User", userId);
        return await client.SendAsync(request);
    }
}
