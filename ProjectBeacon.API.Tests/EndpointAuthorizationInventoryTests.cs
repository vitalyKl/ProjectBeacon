namespace ProjectBeacon.API.Tests;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.API.Auth;

[Collection("api-http")]
public sealed class EndpointAuthorizationInventoryTests
{
    [Fact]
    public void EveryV1RouteHasExplicitAuthorizationClassification()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        var source = factory.Services.GetRequiredService<EndpointDataSource>();

        var routes = EndpointAuthorizationInventory.Create(source);

        Assert.NotEmpty(routes);
        var unclassified = routes.Where(route => route.Policy == "UNCLASSIFIED").ToArray();
        Assert.True(unclassified.Length == 0,
            "Every /v1 endpoint must declare AllowAnonymous, RequireAuthorization, or typed actor metadata.\n"
            + EndpointAuthorizationInventory.ToMarkdown(unclassified));

        Assert.Contains(routes, route =>
            route.Method == "GET" && route.Route == "/v1/version" && route.Policy == "Anonymous");
        Assert.Contains(routes, route =>
            route.Method == "GET" && route.Route == "/v1/auth/me" && route.Policy == "Human");
        Assert.Contains(routes, route =>
            route.Method == "POST" && route.Route == "/v1/devices/me/heartbeat" && route.Policy == "Device");
        Assert.Contains(routes, route =>
            route.Method == "POST" && route.Route == "/v1/chat/sessions/{id:guid}/parts" && route.Policy == "Device");
        Assert.Contains(routes, route =>
            route.Method == "GET" && route.Route == "/v1/devices/me/provider-connections" && route.Policy == "Device");

        Assert.Contains(routes, route =>
            route.Method == "POST" && route.Route == "/v1/projects/{projectId:guid}/git-sync" && route.Policy == "Human");
        Assert.Contains(routes, route =>
            route.Method == "GET" && route.Route == "/v1/projects/{projectId:guid}/git-sync/{commandId:guid}" && route.Policy == "Human");

        Assert.Contains(routes, route =>
            route.Method == "POST" && route.Route == "/v1/tasks/{taskId:guid}/pipeline/start"
            && route.Policy == "Human or API token (SessionDrive)");
        Assert.Contains(routes, route =>
            route.Method == "POST" && route.Route == "/v1/tasks/{taskId:guid}/pipeline/approve"
            && route.Policy == "Human");
        Assert.Contains(routes, route =>
            route.Method == "POST" && route.Route == "/v1/projects/{projectId}/context/drafts"
            && route.Policy == "Human");
        Assert.Contains(routes, route =>
            route.Method == "POST" && route.Route == "/v1/projects/{projectId:guid}/constraints/propose"
            && route.Policy == "Human");
        Assert.Contains(routes, route =>
            route.Method == "POST" && route.Route == "/v1/projects/{projectId:guid}/decisions/propose"
            && route.Policy == "Human");

    }

    [Fact]
    public void InventoryCanRenderStableMarkdownMatrix()
    {
        using var factory = new AuthApiFactory();
        using var client = factory.CreateClient();
        var routes = EndpointAuthorizationInventory.Create(
            factory.Services.GetRequiredService<EndpointDataSource>());

        var markdown = EndpointAuthorizationInventory.ToMarkdown(routes);

        Assert.StartsWith("| Method | Route | Route policy |", markdown, StringComparison.Ordinal);
        Assert.Contains("| POST | `/v1/devices/me/heartbeat` | Device |", markdown, StringComparison.Ordinal);
        Assert.Contains("| POST | `/v1/auth/login` | Anonymous |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("UNCLASSIFIED", markdown, StringComparison.Ordinal);
    }
}
