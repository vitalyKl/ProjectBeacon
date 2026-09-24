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

        using var response = await client.GetAsync("/v1/__test/unhandled");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(500, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", doc.RootElement.GetProperty("title").GetString());
        Assert.False(doc.RootElement.TryGetProperty("detail", out _));
        Assert.False(doc.RootElement.TryGetProperty("exceptionType", out _));
        Assert.DoesNotContain("test unhandled exception", body);
        Assert.DoesNotContain("InvalidOperationException", body);
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
