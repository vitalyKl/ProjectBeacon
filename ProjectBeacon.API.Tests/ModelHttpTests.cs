namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

[Collection("api-http")]
public sealed class ModelHttpTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task ModelCrud_FullFlow()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();
        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "rw",
            capabilities = 3
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("tokenPrefix").GetString()!);

        var empty = await client.GetFromJsonAsync<JsonElement>("/v1/models");
        Assert.Equal(0, empty.GetProperty("backends").GetArrayLength());

        var created = await client.PostAsJsonAsync("/v1/models", new
        {
            id = (Guid?)null,
            name = "m1",
            backendType = "LlamaCpp",
            launchCommand = "llama-server -m m.gguf",
            contextSize = 8192,
            ttl = 300,
            extraFlags = new[] { "--no-mmap" }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.StartsWith("/v1/models/", created.Headers.Location?.ToString());
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var modelId = createdBody.GetProperty("id").GetGuid();

        var read = await client.GetFromJsonAsync<JsonElement>("/v1/models");
        Assert.Single(read.GetProperty("backends").EnumerateArray());
        Assert.Equal("m1", read.GetProperty("backends")[0].GetProperty("name").GetString());

        var updated = await client.PostAsJsonAsync("/v1/models", new
        {
            id = modelId,
            name = "m1-upd",
            backendType = "OpenAiCompatible",
            launchCommand = "http://127.0.0.1:1234/v1",
            contextSize = 4096,
            ttl = 0
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedBody = await updated.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("m1-upd", updatedBody.GetProperty("name").GetString());

        var bind = await client.PostAsJsonAsync("/v1/models/bind", new { role = "planner", modelBackendId = modelId });
        Assert.Equal(HttpStatusCode.OK, bind.StatusCode);

        var blocked = await client.DeleteAsync($"/v1/models/{modelId}");
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        var blockedBody = await blocked.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Contains("planner", blockedBody.GetProperty("error").GetString());

        var unbind = await client.DeleteAsync("/v1/models/bind/planner");
        Assert.Equal(HttpStatusCode.NoContent, unbind.StatusCode);

        var del = await client.DeleteAsync($"/v1/models/{modelId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var missing = await client.DeleteAsync($"/v1/models/{modelId}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/v1/models/proxy/status");
        Assert.False(status.GetProperty("available").GetBoolean());

        var reload = await client.PostAsync("/v1/models/proxy/reload", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, reload.StatusCode);
    }

    [Fact]
    public async Task ReadToken_CannotWriteModels()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();
        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "ro",
            capabilities = 1
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("tokenPrefix").GetString()!);

        var read = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var write = await client.PostAsJsonAsync("/v1/models", new
        {
            name = "x",
            backendType = "LlamaCpp",
            launchCommand = "c"
        });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        var bind = await client.PostAsJsonAsync("/v1/models/bind", new { role = "actor", modelBackendId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, bind.StatusCode);

        var status = await client.GetAsync("/v1/models/proxy/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        var reload = await client.PostAsync("/v1/models/proxy/reload", null);
        Assert.Equal(HttpStatusCode.Forbidden, reload.StatusCode);
    }

    [Fact]
    public async Task Unbind_UnknownRole_BadRequest()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();
        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "rw",
            capabilities = 3
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("tokenPrefix").GetString()!);

        var bad = await client.DeleteAsync("/v1/models/bind/notarole");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Contains("Unknown role", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Registry_ViaJwtAndProjectHeader()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("X-Project-Id", projectId.ToString());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(projectId, body.GetProperty("projectId").GetGuid());
    }

    private static async Task<string> BootstrapAndLogin(HttpClient client)
    {
        var bootRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/bootstrap");
        bootRequest.Content = JsonContent.Create(new { });
        bootRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AuthApiFactory.BootstrapToken);
        var boot = await client.SendAsync(bootRequest);
        boot.EnsureSuccessStatusCode();
        var bootJson = await boot.Content.ReadFromJsonAsync<JsonElement>(Json);
        var password = bootJson.GetProperty("password").GetString();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { login = "admin", password });
        login.EnsureSuccessStatusCode();
        var loginJson = await login.Content.ReadFromJsonAsync<JsonElement>(Json);
        return loginJson.GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} {response.StatusCode} {text}");
        return JsonSerializer.Deserialize<JsonElement>(text, Json);
    }
}
