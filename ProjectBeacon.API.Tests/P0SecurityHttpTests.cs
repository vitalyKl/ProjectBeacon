namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[Collection("api-http")]
public sealed class P0SecurityHttpTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Logout_WithoutAuth_Returns401()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddComment_SpoofedUserId_UsesPrincipal()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var realUserId = me.GetProperty("id").GetGuid();

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();
        var task = await PostJson(client, $"/v1/projects/{projectId}/tasks", new
        {
            title = "sec-test",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = task.GetProperty("id").GetGuid();

        var spoofedId = Guid.NewGuid();
        var comment = await client.PostAsJsonAsync($"/v1/projects/{projectId}/tasks/{taskId}/comments", new
        {
            taskId,
            content = "spoofoo",
            userId = spoofedId
        });
        Assert.True(comment.IsSuccessStatusCode, await comment.Content.ReadAsStringAsync());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        using (TenantScope.EnterUnscoped())
        {
            var stored = await db.TaskComments
                .FirstOrDefaultAsync(c => c.TaskId == taskId && c.Content == "spoofoo");
            Assert.NotNull(stored);
            Assert.Equal(realUserId, stored.UserId);
            Assert.NotEqual(spoofedId, stored.UserId);
        }
    }

    [Fact]
    public async Task CreateTask_SpoofedActorUserId_Ignored()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var realUserId = me.GetProperty("id").GetGuid();

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var spoofedActor = Guid.NewGuid();
        var response = await client.PostAsJsonAsync($"/v1/projects/{projectId}/tasks", new
        {
            title = "spoofoo-task",
            projectId,
            priority = 1,
            type = 3,
            actorUserId = spoofedActor
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var taskId = body.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, taskId);
    }

    [Fact]
    public async Task FinishWork_SpoofedActorId_UsesPrincipal()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var realUserId = me.GetProperty("id").GetGuid();

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();
        var task = await PostJson(client, $"/v1/projects/{projectId}/tasks", new
        {
            title = "fw-test",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = task.GetProperty("id").GetGuid();

        var spoofedActor = Guid.NewGuid().ToString();
        Guid proofId;
        await using (var seed = factory.Services.CreateAsyncScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<BeaconDbContext>();
            using (TenantScope.EnterUnscoped())
            {
                var proof = ReviewRun.Start(projectId, taskId, Domain.Enums.ReviewerType.Agent, Guid.NewGuid());
                proof.Complete("checked", "check:spoof");
                proofId = proof.Id;
                seedDb.ReviewRuns.Add(proof);
                await seedDb.SaveChangesAsync();
            }
        }

        var response = await client.PostAsJsonAsync("/v1/work/finish_work", new
        {
            taskId = taskId.ToString(),
            result = "done",
            output = "spoofoo output",
            actorId = spoofedActor,
            review = new { reviewerRun = true, regressionsFound = 0, regressionsFixed = 0 },
            reviewRunId = proofId
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        using (TenantScope.EnterUnscoped())
        {
            var entity = await db.Tasks.FindAsync([taskId]);
            Assert.NotNull(entity);
            Assert.Equal(Domain.Enums.TaskItemStatus.Done, entity.Status);

            var comment = await db.TaskComments
                .FirstOrDefaultAsync(c => c.TaskId == taskId && c.Content.Contains("Work finished"));
            Assert.NotNull(comment);
            Assert.Equal(realUserId, comment.UserId);
        }
    }

    [Fact]
    public async Task CreateToken_CreatedByDerivedFromPrincipal()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var realUserId = me.GetProperty("id").GetGuid();

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "sec-token",
            capabilities = 1
        });
        var tokenId = token.GetProperty("id").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        using (TenantScope.EnterUnscoped())
        {
            var stored = await db.ApiTokens.FindAsync([tokenId]);
            Assert.NotNull(stored);
            Assert.Equal(realUserId, stored.CreatedByUserId);
        }
    }

    [Fact]
    public async Task CreateOrg_SpoofedCreatedByUserId_Ignored()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var realUserId = me.GetProperty("id").GetGuid();

        var spoofedId = Guid.NewGuid();
        var org = await PostJson(client, "/v1/orgs", new { name = "SpoofOrg", createdByUserId = spoofedId });
        var orgId = org.GetProperty("id").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        using (TenantScope.EnterUnscoped())
        {
            var member = await db.OrgMembers.FirstOrDefaultAsync(m => m.OrgId == orgId);
            Assert.NotNull(member);
            Assert.Equal(realUserId, member.UserId);
            Assert.NotEqual(spoofedId, member.UserId);
        }
    }

    [Fact]
    public async Task CreateProject_SpoofedCreatedByUserId_Ignored()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var realUserId = me.GetProperty("id").GetGuid();

        var org = await PostJson(client, "/v1/orgs", new { name = "Org2" });
        var orgId = org.GetProperty("id").GetGuid();

        var spoofedId = Guid.NewGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "SpoofProj", orgId, createdByUserId = spoofedId });
        var projectId = project.GetProperty("id").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        using (TenantScope.EnterUnscoped())
        {
            var member = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == projectId);
            Assert.NotNull(member);
            Assert.Equal(realUserId, member.UserId);
            Assert.NotEqual(spoofedId, member.UserId);
        }
    }

    [Fact]
    public async Task CreateToken_SpoofedCreatedByUserId_Ignored()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me");
        var realUserId = me.GetProperty("id").GetGuid();

        var org = await PostJson(client, "/v1/orgs", new { name = "Org3" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P3", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var spoofedId = Guid.NewGuid();
        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "spoof-token",
            capabilities = 1,
            createdByUserId = spoofedId
        });
        var tokenId = token.GetProperty("id").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        using (TenantScope.EnterUnscoped())
        {
            var stored = await db.ApiTokens.FindAsync([tokenId]);
            Assert.NotNull(stored);
            Assert.Equal(realUserId, stored.CreatedByUserId);
            Assert.NotEqual(spoofedId, stored.CreatedByUserId);
        }
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
