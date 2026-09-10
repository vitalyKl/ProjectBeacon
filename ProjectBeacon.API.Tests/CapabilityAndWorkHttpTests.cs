namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

[Collection("api-http")]
public sealed class CapabilityAndWorkHttpTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task TaskReadToken_CannotCreateTask()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", description = "d", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "read-only",
            capabilities = 1
        });
        var raw = token.GetProperty("tokenPrefix").GetString()
                  ?? token.GetProperty("TokenPrefix").GetString();
        Assert.False(string.IsNullOrEmpty(raw));
        Assert.StartsWith("bcn_", raw);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        var create = await client.PostAsJsonAsync($"/v1/projects/{projectId}/tasks", new
        {
            title = "x",
            description = "y",
            projectId,
            priority = 1,
            type = 3
        });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task FinishWork_Done_RequiresStructuredReview()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();
        var task = await PostJson(client, $"/v1/projects/{projectId}/tasks", new
        {
            title = "t",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = task.GetProperty("id").GetGuid();

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var actorId = me.GetProperty("id").GetGuid().ToString();

        var missing = await client.PostAsJsonAsync("/v1/work/finish_work", new
        {
            taskId = taskId.ToString(),
            result = "done",
            output = "no review",
            actorId
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var ok = await client.PostAsJsonAsync("/v1/work/finish_work", new
        {
            taskId = taskId.ToString(),
            result = "done",
            output = "reviewed",
            actorId,
            review = new { reviewerRun = true, regressionsFound = 0, regressionsFixed = 0 }
        });
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            using (TenantScope.EnterUnscoped())
            {
                var entity = await db.Tasks.FindAsync([taskId]);
                Assert.NotNull(entity);
                Assert.Equal(Domain.Enums.TaskItemStatus.Done, entity.Status);
            }
        }
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
