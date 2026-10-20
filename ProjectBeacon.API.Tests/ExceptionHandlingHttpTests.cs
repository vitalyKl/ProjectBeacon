namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

[Collection("api-http")]
public sealed class ExceptionHandlingHttpTests
{
    [Fact]
    public async Task Unhandled_Exception_Returns_500_ProblemDetails_Without_StackTrace()
    {
        await using var factory = new TestingApiFactory();
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/__test/unhandled", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(500, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Internal Server Error", doc.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
        Assert.False(doc.RootElement.TryGetProperty("detail", out _));
        Assert.False(doc.RootElement.TryGetProperty("exceptionType", out _));
        Assert.DoesNotContain("test unhandled exception", body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }

    // A BadHttpRequestException is a client fault: the handler must surface its own
    // status code (400) as problem+json, not mask it as a 500.
    [Fact]
    public async Task BadRequest_Exception_Returns_400_ProblemDetails()
    {
        await using var factory = new TestingApiFactory();
        var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/__test/bad-request",
            new StringContent(string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(400, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Bad Request", doc.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
        Assert.False(doc.RootElement.TryGetProperty("detail", out _));
        Assert.False(doc.RootElement.TryGetProperty("exceptionType", out _));
    }

    // A client fault with a non-400 status (413, 415) keeps its own status and the
    // matching RFC 9110 title instead of being reported as a server error.
    [Fact]
    public async Task BadRequest_Exception_Non400Status_Keeps_Title()
    {
        await using var factory = new TestingApiFactory();
        var client = factory.CreateClient();

        foreach (var (status, title) in new (int, string)[]
        {
            (413, "Payload Too Large"),
            (415, "Unsupported Media Type")
        })
        {
            using var response = await client.PostAsync(
                $"/v1/__test/bad-request?status={status}",
                new StringContent(string.Empty),
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(status, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(status, doc.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(title, doc.RootElement.GetProperty("title").GetString());
        }
    }
}

public sealed class TestingApiFactory : AuthApiFactory
{
    public TestingApiFactory() : base(configureBootstrapToken: false)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Testing");
    }
}
