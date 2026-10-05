namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

[Collection("api-http")]
public sealed class ActorRequirementMatrixHttpTests
{
    private const long TaskRead = 1;
    private const long TaskWrite = 2;
    private const long ContextRead = 8;
    private const long Admin = 16;

    [Fact]
    public async Task Human_RequireHuman_Passes()
    {
        await using var ctx = await SetupAsync();
        var resp = await ctx.Client.GetAsync("/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Human_RequireDevice_Returns401()
    {
        await using var ctx = await SetupAsync();
        var resp = await ctx.Client.PostAsJsonAsync("/v1/devices/me/heartbeat", new { probeJson = "{}" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Human_HumanOrApiToken_TaskRead_Passes()
    {
        await using var ctx = await SetupAsync();
        var resp = await ctx.Client.GetAsync($"/v1/projects/{ctx.ProjectId}/tasks");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Human_HumanOrApiToken_ContextRead_Passes()
    {
        await using var ctx = await SetupAsync();
        var resp = await ctx.Client.GetAsync($"/v1/projects/{ctx.ProjectId}/context/nodes");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ApiToken_FullCaps_RequireHuman_Returns403()
    {
        await using var ctx = await SetupAsync();
        await CreateTokenAsync(ctx.Client, ctx.ProjectId, TaskRead | TaskWrite | ContextRead | Admin);

        var resp = await ctx.Client.GetAsync("/v1/auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var projects = await ctx.Client.GetAsync("/v1/projects");
        Assert.Equal(HttpStatusCode.Forbidden, projects.StatusCode);
    }

    [Fact]
    public async Task ApiToken_FullCaps_RequireDevice_Returns401()
    {
        await using var ctx = await SetupAsync();
        await CreateTokenAsync(ctx.Client, ctx.ProjectId, TaskRead | TaskWrite | ContextRead | Admin);

        var resp = await ctx.Client.PostAsJsonAsync("/v1/devices/me/heartbeat", new { probeJson = "{}" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ApiToken_FullCaps_HumanOrApiToken_TaskRead_Passes()
    {
        await using var ctx = await SetupAsync();
        await CreateTokenAsync(ctx.Client, ctx.ProjectId, TaskRead);

        var resp = await ctx.Client.GetAsync($"/v1/projects/{ctx.ProjectId}/tasks");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ApiToken_FullCaps_HumanOrApiToken_ContextRead_Passes()
    {
        await using var ctx = await SetupAsync();
        await CreateTokenAsync(ctx.Client, ctx.ProjectId, TaskRead | TaskWrite | ContextRead | Admin);

        var resp = await ctx.Client.GetAsync($"/v1/projects/{ctx.ProjectId}/context/nodes");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ApiToken_NoContextRead_HumanOrApiToken_ContextRead_Returns403()
    {
        await using var ctx = await SetupAsync();
        await CreateTokenAsync(ctx.Client, ctx.ProjectId, TaskRead);

        var resp = await ctx.Client.GetAsync($"/v1/projects/{ctx.ProjectId}/context/nodes");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ApiToken_NoTaskWrite_HumanOrApiToken_TaskWrite_Returns403()
    {
        await using var ctx = await SetupAsync();
        await CreateTokenAsync(ctx.Client, ctx.ProjectId, TaskRead);

        var resp = await ctx.Client.PostAsJsonAsync($"/v1/projects/{ctx.ProjectId}/tasks", new
        {
            title = "t",
            projectId = ctx.ProjectId,
            priority = 1,
            type = 3
        });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
    [Fact]
    public async Task Device_RequireHuman_Returns403()
    {
        await using var ctx = await SetupAsync();
        var deviceClient = await CreateDeviceClientAsync(ctx);

        var resp = await deviceClient.GetAsync("/v1/auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Device_RequireDevice_Passes()
    {
        await using var ctx = await SetupAsync();
        var deviceClient = await CreateDeviceClientAsync(ctx);

        var resp = await deviceClient.PostAsJsonAsync("/v1/devices/me/heartbeat", new { probeJson = "{}" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Device_HumanOrApiToken_Returns403()
    {
        await using var ctx = await SetupAsync();
        var deviceClient = await CreateDeviceClientAsync(ctx);

        var tasks = await deviceClient.GetAsync($"/v1/projects/{ctx.ProjectId}/tasks");
        Assert.Equal(HttpStatusCode.Forbidden, tasks.StatusCode);

        var nodes = await deviceClient.GetAsync($"/v1/projects/{ctx.ProjectId}/context/nodes");
        Assert.Equal(HttpStatusCode.Forbidden, nodes.StatusCode);
    }

    [Fact]
    public async Task Anonymous_RequireHuman_Returns401()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_RequireDevice_Returns401()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/devices/me/heartbeat", new { probeJson = "{}" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Anonymous_HumanOrApiToken_Returns401()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var projectId = Guid.NewGuid();

        var tasks = await client.GetAsync($"/v1/projects/{projectId}/tasks");
        Assert.Equal(HttpStatusCode.Unauthorized, tasks.StatusCode);

        var nodes = await client.GetAsync($"/v1/projects/{projectId}/context/nodes");
        Assert.Equal(HttpStatusCode.Unauthorized, nodes.StatusCode);
    }

    [Fact]
    public async Task Anonymous_AllowAnonymous_Options_Passes()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/v1/auth/options");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
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
            name = $"matrix-{Guid.NewGuid():N}",
            capabilities
        });
        var raw = token.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(raw));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw!);
        return raw!;
    }

    private static async Task<HttpClient> CreateDeviceClientAsync(Setup ctx)
    {
        var created = await ctx.Client.PostAsJsonAsync("/v1/devices", new { name = $"dev-{Guid.NewGuid():N}", fingerprint = $"fp-{Guid.NewGuid():N}" });
        created.EnsureSuccessStatusCode();
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("token").GetString();

        var deviceClient = ctx.Factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!);
        return deviceClient;
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
