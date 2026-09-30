namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[Collection("api-http")]
public sealed class P2AuthorizationHttpTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task AddMember_NonMember_Returns403()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var adminJwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var outsiderJwt = await RegisterAndLogin(client, "outsider1", "o1@beacon.local");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderJwt);

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var outsiderId = me.GetProperty("id").GetGuid();

        var response = await client.PostAsJsonAsync($"/v1/projects/{projectId}/members", new
        {
            projectId,
            userId = outsiderId,
            role = 1
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AddMember_ProjectOwner_Succeeds()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var adminJwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var outsiderJwt = await RegisterAndLogin(client, "outsider2", "o2@beacon.local");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderJwt);
        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var outsiderId = me.GetProperty("id").GetGuid();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
        var response = await client.PostAsJsonAsync($"/v1/projects/{projectId}/members", new
        {
            projectId,
            userId = outsiderId,
            role = 1
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RemoveMember_NonMember_CannotRemoveOther_Returns403()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var adminJwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var outsiderJwt = await RegisterAndLogin(client, "outsider3", "o3@beacon.local");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderJwt);
        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var outsiderId = me.GetProperty("id").GetGuid();

        var adminMe = new AuthenticationHeaderValue("Bearer", adminJwt);
        client.DefaultRequestHeaders.Authorization = adminMe;
        var adminInfo = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var adminId = adminInfo.GetProperty("id").GetGuid();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderJwt);
        var response = await client.DeleteAsync($"/v1/projects/{projectId}/members/{adminId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RemoveMember_SelfRemoval_Succeeds()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var adminJwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var outsiderJwt = await RegisterAndLogin(client, "outsider4", "o4@beacon.local");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderJwt);
        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var outsiderId = me.GetProperty("id").GetGuid();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
        await PostJson(client, $"/v1/projects/{projectId}/members", new
        {
            projectId,
            userId = outsiderId,
            role = 1
        });

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderJwt);
        var response = await client.DeleteAsync($"/v1/projects/{projectId}/members/{outsiderId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CreateToken_NonMember_Returns403()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var adminJwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var outsiderJwt = await RegisterAndLogin(client, "outsider5", "o5@beacon.local");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderJwt);

        var response = await client.PostAsJsonAsync($"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "hacker-token",
            capabilities = 1
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateToken_ProjectOwner_Succeeds()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var adminJwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var response = await client.PostAsJsonAsync($"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "legit-token",
            capabilities = 1
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RevokeToken_NonMember_Returns403()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var adminJwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "to-revoke",
            capabilities = 1
        });
        var tokenId = token.GetProperty("id").GetGuid();

        var outsiderJwt = await RegisterAndLogin(client, "outsider6", "o6@beacon.local");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderJwt);

        var response = await client.DeleteAsync($"/v1/projects/{projectId}/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RevokeToken_ProjectOwner_Succeeds()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var adminJwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "to-revoke-ok",
            capabilities = 1
        });
        var tokenId = token.GetProperty("id").GetGuid();

        var response = await client.DeleteAsync($"/v1/projects/{projectId}/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ApiToken_CannotCreateTokens_Returns403()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var adminJwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var tokenResponse = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "api-token",
            capabilities = 1
        });
        var apiTokenValue = tokenResponse.GetProperty("tokenPrefix").GetString()!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiTokenValue);
        var response = await client.PostAsJsonAsync($"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "escalated",
            capabilities = 1
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ApiToken_CannotAddMembers_Returns403()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var adminJwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var tokenResponse = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "api-token-m",
            capabilities = 1
        });
        var apiTokenValue = tokenResponse.GetProperty("tokenPrefix").GetString()!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiTokenValue);
        var outsiderId = Guid.NewGuid();
        var response = await client.PostAsJsonAsync($"/v1/projects/{projectId}/members", new
        {
            projectId,
            userId = outsiderId,
            role = 1
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

    private static async Task<string> RegisterAndLogin(HttpClient client, string login, string email)
    {
        var oldAuth = client.DefaultRequestHeaders.Authorization?.ToString();
        client.DefaultRequestHeaders.Authorization = null;
        try
        {
            var register = await client.PostAsJsonAsync("/v1/auth/register", new
            {
                login,
                email,
                password = "Passw0rd!x"
            });
            Assert.True(register.IsSuccessStatusCode, await register.Content.ReadAsStringAsync());
            var loginResponse = await client.PostAsJsonAsync("/v1/auth/login", new { login, password = "Passw0rd!x" });
            Assert.True(loginResponse.IsSuccessStatusCode, await loginResponse.Content.ReadAsStringAsync());
            var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
            return loginJson.GetProperty("token").GetString()!;
        }
        finally
        {
            if (oldAuth is not null)
                client.DefaultRequestHeaders.Authorization = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(oldAuth);
        }
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} {response.StatusCode} {text}");
        return JsonSerializer.Deserialize<JsonElement>(text, Json);
    }
}
