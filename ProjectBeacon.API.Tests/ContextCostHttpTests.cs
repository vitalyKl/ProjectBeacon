namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.Entities.Evals;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

[Collection("api-http")]
public sealed class ContextCostHttpTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task ContextCost_ReturnsPairedRuns()
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
            title = "Eval task",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = task.GetProperty("id").GetGuid();

        var pairId = Guid.NewGuid().ToString("n");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            using (TenantScope.EnterUnscoped())
            {
                var withBrief = EvalRun.Create(projectId, taskId, pairId, EvalCondition.WithBrief);
                withBrief.Complete(100, 200, 5, true, "ref://1");
                var withoutBrief = EvalRun.Create(projectId, taskId, pairId, EvalCondition.WithoutBrief);
                withoutBrief.Complete(50, 100, 3, false, "ref://2");
                db.EvalRuns.Add(withBrief);
                db.EvalRuns.Add(withoutBrief);
                await db.SaveChangesAsync();
            }
        }

        var response = await client.GetAsync($"/v1/reports/context-cost/{taskId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(taskId, body.GetProperty("taskId").GetGuid());
        Assert.Equal(projectId, body.GetProperty("projectId").GetGuid());
        Assert.Equal("Eval task", body.GetProperty("taskTitle").GetString());
        Assert.Equal(1, body.GetProperty("pairCount").GetInt32());
        var pairs = body.GetProperty("pairs");
        Assert.Equal(1, pairs.GetArrayLength());
        var pair = pairs[0];
        Assert.Equal(pairId, pair.GetProperty("pairId").GetString());
        Assert.Equal(50, pair.GetProperty("promptTokensDelta").GetInt32());
        Assert.Equal(100, pair.GetProperty("completionTokensDelta").GetInt32());
        Assert.Equal(150, pair.GetProperty("totalTokensDelta").GetInt32());
        Assert.Equal(2, pair.GetProperty("turnCountDelta").GetInt32());
        Assert.True(pair.GetProperty("withBriefPassed").GetBoolean());
        Assert.False(pair.GetProperty("withoutBriefPassed").GetBoolean());
    }

    [Fact]
    public async Task ContextCost_NoTask_ReturnsNotFound()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await client.GetAsync($"/v1/reports/context-cost/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ContextCost_NoPairedRuns_ReturnsEmptyPairs()
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
            title = "No pairs",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = task.GetProperty("id").GetGuid();

        var response = await client.GetAsync($"/v1/reports/context-cost/{taskId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(0, body.GetProperty("pairCount").GetInt32());
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
        return JsonSerializer.Deserialize<JsonElement>(text, Json)!;
    }
}
