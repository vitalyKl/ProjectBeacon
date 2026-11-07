namespace ProjectBeacon.Cli.Tests.ModelSwapping;

using System.Net;
using ProjectBeacon.Cli.Client.ModelSwapping;

public sealed class LlamaServerProtocolFixtureTests
{
    [Fact]
    public async Task HealthCheck_ReadyFixtureReturnsHealthy()
    {
        var paths = new List<string>();
        using var handler = new FixtureHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ProtocolFixtureFiles.Read("LlamaServer/health-ready.json"))
            };
        });
        using var backend = new LlamaServerBackend(handler)
        {
            Name = "fixture",
            Port = 8080
        };

        var healthy = await backend.HealthCheckAsync(CancellationToken.None);

        Assert.True(healthy);
        Assert.Equal(new[] { "/health" }, paths);
    }

    [Fact]
    public async Task HealthCheck_LoadingFixtureFallsBackAndRemainsUnhealthy()
    {
        var paths = new List<string>();
        using var handler = new FixtureHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/health" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(ProtocolFixtureFiles.Read("LlamaServer/health-loading.json"))
                },
                "/v1/models" => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var backend = new LlamaServerBackend(handler)
        {
            Name = "fixture",
            Port = 8081
        };

        var healthy = await backend.HealthCheckAsync(CancellationToken.None);

        Assert.False(healthy);
        Assert.Equal(new[] { "/health", "/v1/models" }, paths);
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
