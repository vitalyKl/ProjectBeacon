namespace ProjectBeacon.Web.Tests;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using ProjectBeacon.Infrastructure.Http;

/// <summary>
/// Verifies the auth rate-limit partition keys on the client IP behind a trusted proxy
/// (X-Forwarded-For), not on the proxy's remote address. The focused app mirrors the
/// production middleware order from MiddlewareExtensions: UseForwardedHeaders before
/// UseRouting and UseRateLimiter.
/// </summary>
public sealed class RateLimitPartitioningHttpTests
{
    private const string ProxyIp = "203.0.113.1";
    private const string ClientA = "203.0.113.10";
    private const string ClientB = "203.0.113.20";

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
    public async Task NoForwardedFor_FallsBackToProxyRemoteIp()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(client, null)).StatusCode);
    }

    private static async Task<WebApplication> BuildAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(options => AuthRateLimiter.Configure(options, 5, 60, 60));

        var app = builder.Build();

        // TestServer assigns no socket remote endpoint; simulate the trusted-proxy hop.
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(ProxyIp);
            await next(context);
        });
        var forwardedOptions = new ForwardedHeadersOptions
        {
            // Mirrors the flag selection in MiddlewareExtensions.
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        // The explicit trust list replaces the framework loopback defaults.
        forwardedOptions.KnownNetworks.Clear();
        forwardedOptions.KnownNetworks.Add(
            new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse(ProxyIp), 32));
        app.UseForwardedHeaders(forwardedOptions);
        app.UseRouting();
        app.UseRateLimiter();
        app.MapPost("/auth-test", () => Results.Ok()).RequireRateLimiting("auth");

        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth-test");
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request);
    }
}
