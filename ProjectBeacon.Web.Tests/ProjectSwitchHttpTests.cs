namespace ProjectBeacon.Web.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

public sealed class ProjectSwitchHttpTests
{
    [Fact]
    public async Task SwitchProject_ReissuesCookieAndRedirectsToReturnUrl()
    {
        var (factory, client, projectId) = await SetupAsync();
        using var _ = factory;

        var response = await client.GetAsync($"/project/switch?projectId={projectId}&returnUrl=/board");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/board", response.Headers.Location?.ToString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("BeaconAuth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchProject_RejectsUnknownProject()
    {
        var (factory, client, _) = await SetupAsync();
        using var _ = factory;

        var response = await client.GetAsync($"/project/switch?projectId={Guid.NewGuid()}&returnUrl=/board");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task SwitchProject_RejectsOpenRedirectReturnUrl()
    {
        var (factory, client, projectId) = await SetupAsync();
        using var _ = factory;

        var response = await client.GetAsync($"/project/switch?projectId={projectId}&returnUrl=https%3A%2F%2Fevil.example");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task SwitchProject_Anonymous_ChallengesToLogin()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/project/switch?projectId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(WebTestFactory Factory, HttpClient Client, Guid ProjectId)> SetupAsync()
    {
        var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var boot = await client.PostAsJsonAsync("/v1/auth/bootstrap", new { });
        boot.EnsureSuccessStatusCode();
        var bootJson = await boot.Content.ReadFromJsonAsync<JsonElement>();
        var password = bootJson.GetProperty("password").GetString();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password!
        });
        var login = await client.PostAsync("/v1/auth/cookie-login", form);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var org = await client.PostAsJsonAsync("/v1/orgs", new { name = "Org", description = (string?)null });
        org.EnsureSuccessStatusCode();
        var orgJson = await org.Content.ReadFromJsonAsync<JsonElement>();
        var orgId = orgJson.GetProperty("id").GetGuid();

        var project = await client.PostAsJsonAsync("/v1/projects",
            new { name = "Alpha", description = (string?)null, orgId = orgId.ToString() });
        project.EnsureSuccessStatusCode();
        var projectJson = await project.Content.ReadFromJsonAsync<JsonElement>();

        return (factory, client, projectJson.GetProperty("id").GetGuid());
    }
}
