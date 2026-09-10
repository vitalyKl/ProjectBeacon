namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

[Collection("api-http")]
public sealed class OrgProjectTaskHttpTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task OrgProjectMemberToken_CreateReadList()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var gotOrg = await client.GetFromJsonAsync<JsonElement>($"/v1/orgs/{orgId}");
        Assert.Equal("Org", gotOrg.GetProperty("name").GetString());
        var orgs = await client.GetFromJsonAsync<JsonElement>("/v1/orgs");
        Assert.True(orgs.GetArrayLength() >= 1);

        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();
        var gotProject = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}");
        Assert.Equal("P", gotProject.GetProperty("name").GetString());
        var listed = await client.GetFromJsonAsync<JsonElement>($"/v1/orgs/{orgId}/projects");
        Assert.True(listed.GetArrayLength() >= 1);

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var userId = me.GetProperty("id").GetGuid();
        var member = await PostJson(client, $"/v1/projects/{projectId}/members", new
        {
            projectId,
            userId,
            role = 0
        });
        Assert.Equal(userId, member.GetProperty("userId").GetGuid());
        var members = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/members");
        Assert.True(members.GetArrayLength() >= 1);

        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "ci",
            capabilities = 1
        });
        var raw = token.GetProperty("tokenPrefix").GetString();
        Assert.StartsWith("bcn_", raw);
        var tokenId = token.GetProperty("id").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            var stored = await db.ApiTokens.FindAsync([tokenId]);
            Assert.NotNull(stored);
            Assert.DoesNotContain("bcn_", stored.TokenHash);
            Assert.NotEqual(raw, stored.TokenHash);
        }

        var tokens = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/tokens");
        Assert.True(tokens.GetArrayLength() >= 1);
        var listedPrefix = tokens[0].GetProperty("tokenPrefix").GetString();
        Assert.False(string.IsNullOrEmpty(listedPrefix));
        Assert.False(listedPrefix!.StartsWith("bcn_") && listedPrefix.Length > 12);
    }

    [Fact]
    public async Task Task_CrudCommentsAndDependencies()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var created = await PostJson(client, $"/v1/projects/{projectId}/tasks", new
        {
            title = "A",
            description = "one",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = created.GetProperty("id").GetGuid();
        var other = await PostJson(client, $"/v1/projects/{projectId}/tasks", new
        {
            title = "B",
            projectId,
            priority = 1,
            type = 3
        });
        var otherId = other.GetProperty("id").GetGuid();

        var listed = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/tasks");
        Assert.True(listed.GetArrayLength() >= 2);

        var updated = await client.PutAsJsonAsync($"/v1/tasks/{taskId}", new
        {
            taskId,
            title = "A2",
            description = "two",
            priority = 2,
            type = 3
        });
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
        var got = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/tasks/{taskId}");
        Assert.Equal("A2", got.GetProperty("title").GetString());

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var userId = me.GetProperty("id").GetGuid();
        var comment = await client.PostAsJsonAsync($"/v1/tasks/{taskId}/comments", new
        {
            taskId,
            content = "hello",
            userId
        });
        Assert.True(comment.IsSuccessStatusCode, await comment.Content.ReadAsStringAsync());

        var deps = await client.PutAsJsonAsync($"/v1/tasks/{taskId}/dependencies", new
        {
            taskId,
            dependentTaskIds = new[] { otherId }
        });
        Assert.True(deps.IsSuccessStatusCode, await deps.Content.ReadAsStringAsync());
        var withDeps = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/tasks/{taskId}");
        Assert.Equal(otherId, withDeps.GetProperty("dependencies")[0].GetGuid());

        var deleteDependent = await client.DeleteAsync($"/v1/tasks/{taskId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteDependent.StatusCode);
        var delete = await client.DeleteAsync($"/v1/tasks/{otherId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task LabelsMatch_AndGenerateReport()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var labels = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/labels");
        Assert.True(labels.GetArrayLength() >= 1);
        var match = await client.GetAsync($"/v1/projects/{projectId}/labels/match?path=ProjectBeacon.API/Program.cs");
        Assert.True(match.IsSuccessStatusCode, await match.Content.ReadAsStringAsync());
        var matched = await match.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("API", matched.GetProperty("name").GetString());

        var autoTask = await PostJson(client, $"/v1/projects/{projectId}/tasks", new
        {
            title = "API work",
            projectId,
            priority = 1,
            type = 3,
            path = "ProjectBeacon.API/Program.cs"
        });
        Assert.Equal(matched.GetProperty("id").GetGuid(), autoTask.GetProperty("labelId").GetGuid());

        var report = await PostJson(client, $"/v1/projects/{projectId}/reports", new { createdByType = "user", createdById = "t" });
        Assert.Contains("development report", report.GetProperty("title").GetString());
        var reportId = report.GetProperty("id").GetGuid();
        var got = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/reports/{reportId}");
        Assert.Equal(reportId, got.GetProperty("id").GetGuid());

        var other = await PostJson(client, "/v1/projects", new { name = "Q", orgId });
        var otherId = other.GetProperty("id").GetGuid();
        var cross = await client.GetAsync($"/v1/projects/{otherId}/reports/{reportId}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        var reports = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/reports");
        Assert.True(reports.GetArrayLength() >= 1);

        var compile = await PostJson(client, $"/v1/projects/{projectId}/context/compile", new
        {
            projectId,
            budgetTokens = 8000,
            includeHandoff = false,
            includeChangedScope = false,
            includeTreeCapsule = false
        });
        Assert.Contains("## Tools for this task", compile.GetProperty("briefMarkdown").GetString());
        Assert.True(compile.GetProperty("tokenEstimate").GetInt32() > 0);
    }

    private static async Task<string> BootstrapAndLogin(HttpClient client)
    {
        var boot = await client.PostAsJsonAsync("/v1/auth/bootstrap", new { });
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
