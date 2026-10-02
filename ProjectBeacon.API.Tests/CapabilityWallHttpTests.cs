namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

[Collection("api-http")]
public sealed class CapabilityWallHttpTests
{
    private const long TaskRead = 1;
    private const long TaskWrite = 2;
    private const long ContextRead = 8;
    private const long Admin = 16;

    [Fact]
    public async Task TaskReadToken_CanListTasks_ButCannotStartPipeline()
    {
        await using var ctx = await SetupAsync();
        var client = ctx.Client;
        var token = await CreateTokenAsync(client, ctx.ProjectId, TaskRead);

        var list = await client.GetAsync($"/v1/projects/{ctx.ProjectId}/tasks");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var start = await client.PostAsync($"/v1/tasks/{Guid.NewGuid()}/pipeline/start", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
    }

    [Fact]
    public async Task TaskWriteToken_CanCreateTask_ButCannotReadModels()
    {
        await using var ctx = await SetupAsync();
        var client = ctx.Client;
        var token = await CreateTokenAsync(client, ctx.ProjectId, TaskWrite);

        var create = await client.PostAsJsonAsync($"/v1/projects/{ctx.ProjectId}/tasks", new
        {
            title = "t",
            projectId = ctx.ProjectId,
            priority = 1,
            type = 3
        });
        Assert.True(create.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, await create.Content.ReadAsStringAsync());

        var models = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Forbidden, models.StatusCode);
    }

    [Fact]
    public async Task ContextReadToken_CanReadContext_ButCannotWrite()
    {
        await using var ctx = await SetupAsync();
        var client = ctx.Client;
        var token = await CreateTokenAsync(client, ctx.ProjectId, ContextRead);

        var nodes = await client.GetAsync($"/v1/projects/{ctx.ProjectId}/context/nodes");
        Assert.Equal(HttpStatusCode.OK, nodes.StatusCode);

        var constraints = await client.GetAsync($"/v1/projects/{ctx.ProjectId}/constraints");
        Assert.Equal(HttpStatusCode.OK, constraints.StatusCode);

        var upsert = await client.PostAsJsonAsync($"/v1/projects/{ctx.ProjectId}/context/nodes", new
        {
            title = "n",
            bodyMarkdown = "b",
            scopeType = "Project",
            source = "Native"
        });
        Assert.Equal(HttpStatusCode.Forbidden, upsert.StatusCode);

        var createConstraint = await client.PostAsJsonAsync($"/v1/projects/{ctx.ProjectId}/constraints", new { body = "c", kind = "Security" });
        Assert.Equal(HttpStatusCode.Forbidden, createConstraint.StatusCode);
    }

    [Fact]
    public async Task Token_WithoutContextRead_CannotReadContext()
    {
        await using var ctx = await SetupAsync();
        var client = ctx.Client;
        var token = await CreateTokenAsync(client, ctx.ProjectId, TaskRead);

        var nodes = await client.GetAsync($"/v1/projects/{ctx.ProjectId}/context/nodes");
        Assert.Equal(HttpStatusCode.Forbidden, nodes.StatusCode);
    }

    [Fact]
    public async Task AdminToken_PassesCapabilityRoutes_ButNotHumanOnly()
    {
        await using var ctx = await SetupAsync();
        var client = ctx.Client;
        var token = await CreateTokenAsync(client, ctx.ProjectId, Admin);

        var tasks = await client.GetAsync($"/v1/projects/{ctx.ProjectId}/tasks");
        Assert.Equal(HttpStatusCode.OK, tasks.StatusCode);

        var models = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);

        var orgs = await client.GetAsync("/v1/orgs");
        Assert.Equal(HttpStatusCode.Forbidden, orgs.StatusCode);

        var me = await client.GetAsync("/v1/auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
    }

    [Fact]
    public async Task FullCapabilityToken_ForbiddenOnHumanOnlyDomains()
    {
        await using var ctx = await SetupAsync();
        var client = ctx.Client;
        var token = await CreateTokenAsync(client, ctx.ProjectId, TaskRead | TaskWrite | ContextRead | Admin);

        var routes = new[]
        {
            ("GET", "/v1/orgs"),
            ("GET", "/v1/projects"),
            ("GET", $"/v1/projects/{ctx.ProjectId}/decisions"),
            ("GET", $"/v1/projects/{ctx.ProjectId}/milestones"),
            ("GET", $"/v1/projects/{ctx.ProjectId}/labels"),
            ("GET", $"/v1/projects/{ctx.ProjectId}/reports"),
            ("GET", $"/v1/projects/{ctx.ProjectId}/members"),
            ("GET", $"/v1/projects/{ctx.ProjectId}/tokens"),
            ("GET", "/v1/chat/sessions"),
            ("GET", "/v1/auth/me"),
        };

        foreach (var (method, path) in routes)
        {
            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} -> {response.StatusCode}");
        }
    }

    [Fact]
    public async Task Human_AccessesHumanOnlyRoutes_Without403()
    {
        await using var ctx = await SetupAsync();
        var client = ctx.Client;

        var orgs = await client.GetAsync("/v1/orgs");
        Assert.Equal(HttpStatusCode.OK, orgs.StatusCode);

        var me = await client.GetAsync("/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var create = await client.PostAsJsonAsync("/v1/projects", new { name = "P2", orgId = ctx.OrgId });
        Assert.True(create.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, await create.Content.ReadAsStringAsync());

        var decisions = await client.GetAsync($"/v1/projects/{ctx.ProjectId}/decisions");
        Assert.Equal(HttpStatusCode.OK, decisions.StatusCode);

        var nodes = await client.PostAsJsonAsync($"/v1/projects/{ctx.ProjectId}/context/nodes", new
        {
            title = "n",
            bodyMarkdown = "b",
            scopeType = "Project",
            source = "Native"
        });
        Assert.True(nodes.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, await nodes.Content.ReadAsStringAsync());
    }

    private static async Task<Setup> SetupAsync()
    {
        var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        return new Setup(factory, client, orgId, projectId);
    }

    private static async Task<string> CreateTokenAsync(HttpClient client, Guid projectId, long capabilities)
    {
        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = $"wall-{Guid.NewGuid():N}",
            capabilities
        });
        var raw = token.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(raw));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        return raw!;
    }

    private static async Task<string> BootstrapAndLogin(HttpClient client)
    {
        var bootRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/bootstrap");
        bootRequest.Content = JsonContent.Create(new { });
        bootRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AuthApiFactory.BootstrapToken);
        var boot = await client.SendAsync(bootRequest);
        boot.EnsureSuccessStatusCode();
        var bootJson = await boot.Content.ReadFromJsonAsync<JsonElement>();
        var password = bootJson.GetProperty("password").GetString();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { login = "admin", password });
        login.EnsureSuccessStatusCode();
        var loginJson = await login.Content.ReadFromJsonAsync<JsonElement>();
        return loginJson.GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} {response.StatusCode} {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private sealed record Setup(AuthApiFactory Factory, HttpClient Client, Guid OrgId, Guid ProjectId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
            => await Factory.DisposeAsync();
    }
}
