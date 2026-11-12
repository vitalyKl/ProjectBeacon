namespace ProjectBeacon.Infrastructure.Tests.Operations;

using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ProjectBeacon.Application.Operations;
using ProjectBeacon.DockerLogProxy;

public sealed class DockerLogProxySignatureMiddlewareTests
{
    private const string SecretHex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly byte[] Secret = Convert.FromHexString(SecretHex);
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InvokeAsync_ValidSignedGetInvokesEndpoint()
    {
        var nextInvoked = false;
        var middleware = CreateMiddleware(new FixedTimeProvider(Now), _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });
        var context = SignedRequest(
            HttpMethods.Get,
            "/internal/v1/logs/web",
            "?tail=10",
            "0123456789abcdef0123456789abcdef",
            Now);

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_DuplicateNonceIsRejected()
    {
        var nextInvocations = 0;
        var middleware = CreateMiddleware(new FixedTimeProvider(Now), _ =>
        {
            nextInvocations++;
            return Task.CompletedTask;
        });
        const string nonce = "0123456789abcdef0123456789abcdef";

        var first = SignedRequest(HttpMethods.Get, "/internal/v1/logs/web", "?tail=10", nonce, Now);
        await middleware.InvokeAsync(first);
        var replay = SignedRequest(HttpMethods.Get, "/internal/v1/logs/web", "?tail=10", nonce, Now.AddSeconds(1));

        await middleware.InvokeAsync(replay);

        Assert.Equal(1, nextInvocations);
        Assert.Equal(StatusCodes.Status401Unauthorized, replay.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_InvalidSignatureIsRejected()
    {
        var nextInvoked = false;
        var middleware = CreateMiddleware(new FixedTimeProvider(Now), _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });
        var context = SignedRequest(
            HttpMethods.Get,
            "/internal/v1/logs/web",
            "?tail=10",
            "fedcba9876543210fedcba9876543210",
            Now);
        context.Request.Headers["X-Beacon-Ops-Signature"] = new string('0', 64);

        await middleware.InvokeAsync(context);

        Assert.False(nextInvoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ExpiredTimestampIsRejected()
    {
        var nextInvoked = false;
        var middleware = CreateMiddleware(new FixedTimeProvider(Now), _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });
        var context = SignedRequest(
            HttpMethods.Get,
            "/internal/v1/logs/web",
            "?tail=10",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Now.AddSeconds(-31));

        await middleware.InvokeAsync(context);

        Assert.False(nextInvoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_NonGetMethodIsRejectedAndAdvertisesAllowedMethod()
    {
        var nextInvoked = false;
        var middleware = CreateMiddleware(new FixedTimeProvider(Now), _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/internal/v1/logs/web";

        await middleware.InvokeAsync(context);

        Assert.False(nextInvoked);
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
        Assert.Equal(HttpMethods.Get, context.Response.Headers["Allow"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_UnknownPathIsNotForwarded()
    {
        var nextInvoked = false;
        var middleware = CreateMiddleware(new FixedTimeProvider(Now), _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/internal/v1/containers";

        await middleware.InvokeAsync(context);

        Assert.False(nextInvoked);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_HealthCheckRemainsAvailableWithoutSignature()
    {
        var nextInvoked = false;
        var middleware = CreateMiddleware(new FixedTimeProvider(Now), _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/healthz";

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static DockerLogProxySignatureMiddleware CreateMiddleware(
        TimeProvider timeProvider,
        RequestDelegate next)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DOCKER_SOCKET_PATH"] = Path.Combine(Path.GetTempPath(), "docker.sock"),
                ["DOCKER_LOG_PROXY_COMPOSE_PROJECT"] = "beacon-alpha",
                ["DOCKER_LOG_PROXY_SHARED_SECRET"] = SecretHex
            })
            .Build();
        return new DockerLogProxySignatureMiddleware(
            next,
            DockerLogProxyOptions.FromConfiguration(configuration),
            new ReplayNonceStore(),
            timeProvider);
    }

    private static DefaultHttpContext SignedRequest(
        string method,
        string path,
        string query,
        string nonce,
        DateTimeOffset timestamp)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        var timestampText = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        context.Request.Headers["X-Beacon-Ops-Timestamp"] = timestampText;
        context.Request.Headers["X-Beacon-Ops-Nonce"] = nonce;
        context.Request.Headers["X-Beacon-Ops-Signature"] = PlatformLogRequestSignature.Create(
            Secret,
            timestampText,
            nonce,
            method,
            path + query);
        return context;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
