namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.Entities.Identity;
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
        var gotOrg = await client.GetFromJsonAsync<JsonElement>($"/v1/orgs/{orgId}", TestContext.Current.CancellationToken);
        Assert.Equal("Org", gotOrg.GetProperty("name").GetString());
        var orgs = await client.GetFromJsonAsync<JsonElement>("/v1/orgs", TestContext.Current.CancellationToken);
        Assert.True(orgs.GetArrayLength() >= 1);

        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();
        var gotProject = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}", TestContext.Current.CancellationToken);
        Assert.Equal("P", gotProject.GetProperty("name").GetString());
        var listed = await client.GetFromJsonAsync<JsonElement>($"/v1/orgs/{orgId}/projects", TestContext.Current.CancellationToken);
        Assert.True(listed.GetArrayLength() >= 1);

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me", TestContext.Current.CancellationToken);
        var userId = me.GetProperty("id").GetGuid();
        var members = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/members", TestContext.Current.CancellationToken);
        Assert.True(members.GetArrayLength() >= 1);
        Assert.Equal(userId, members[0].GetProperty("userId").GetGuid());

        Guid extraUserId;
        await using (var seed = factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<BeaconDbContext>();
            var extra = User.Create("member2", "member2@beacon.local", "hash");
            db.Users.Add(extra);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            extraUserId = extra.Id;
        }

        var member = await PostJson(client, $"/v1/projects/{projectId}/members", new
        {
            projectId,
            userId = extraUserId,
            role = 2
        });
        Assert.Equal(extraUserId, member.GetProperty("userId").GetGuid());
        members = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/members", TestContext.Current.CancellationToken);
        Assert.True(members.GetArrayLength() >= 2);

        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "ci",
            capabilities = 1
        });
        var raw = token.GetProperty("token").GetString();
        var createdPrefix = token.GetProperty("tokenPrefix").GetString();
        Assert.StartsWith("bcn_", raw);
        Assert.Equal(8, createdPrefix!.Length);
        Assert.StartsWith(createdPrefix, raw);
        Assert.NotEqual(raw, createdPrefix);
        var tokenId = token.GetProperty("id").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            using (TenantScope.EnterUnscoped())
            {
                var stored = await db.ApiTokens.FindAsync([tokenId], cancellationToken: TestContext.Current.CancellationToken);
                Assert.NotNull(stored);
                Assert.DoesNotContain("bcn_", stored.TokenHash);
                Assert.NotEqual(raw, stored.TokenHash);
            }
        }

        var tokens = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/tokens", TestContext.Current.CancellationToken);
        Assert.True(tokens.GetArrayLength() >= 1);
        var listedPrefix = tokens[0].GetProperty("tokenPrefix").GetString();
        Assert.Equal(createdPrefix, listedPrefix);
        Assert.False(tokens[0].TryGetProperty("token", out _));

        var one = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/tokens/{tokenId}", TestContext.Current.CancellationToken);
        Assert.Equal(createdPrefix, one.GetProperty("tokenPrefix").GetString());
        Assert.False(one.TryGetProperty("token", out _));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        var asToken = await client.GetAsync($"/v1/projects/{projectId}/tasks", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, asToken.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var revoked = await client.DeleteAsync($"/v1/projects/{projectId}/tokens/{tokenId}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        var after = await client.GetAsync($"/v1/projects/{projectId}/tasks", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
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

        var listed = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/tasks", TestContext.Current.CancellationToken);
        Assert.True(listed.GetArrayLength() >= 2);

        var toProgress = await client.PatchAsJsonAsync($"/v1/projects/{projectId}/tasks/{taskId}/status", new { status = "InProgress" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(toProgress.IsSuccessStatusCode, await toProgress.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var missingReview = await client.PatchAsJsonAsync($"/v1/projects/{projectId}/tasks/{taskId}/status", new { status = "Done" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingReview.StatusCode);

        var updated = await client.PutAsJsonAsync($"/v1/projects/{projectId}/tasks/{taskId}", new
        {
            taskId,
            title = "A2",
            description = "two",
            priority = 2,
            type = 3
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var got = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/tasks/{taskId}", TestContext.Current.CancellationToken);
        Assert.Equal("A2", got.GetProperty("title").GetString());

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me", TestContext.Current.CancellationToken);
        var userId = me.GetProperty("id").GetGuid();
        var comment = await client.PostAsJsonAsync($"/v1/projects/{projectId}/tasks/{taskId}/comments", new
        {
            taskId,
            content = "hello",
            userId
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(comment.IsSuccessStatusCode, await comment.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));

        var deps = await client.PutAsJsonAsync($"/v1/projects/{projectId}/tasks/{taskId}/dependencies", new
        {
            taskId,
            dependentTaskIds = new[] { otherId }
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(deps.IsSuccessStatusCode, await deps.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var withDeps = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/tasks/{taskId}", TestContext.Current.CancellationToken);
        Assert.Equal(otherId, withDeps.GetProperty("dependencies")[0].GetGuid());

        var deleteDependent = await client.DeleteAsync($"/v1/projects/{projectId}/tasks/{taskId}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteDependent.StatusCode);
        var delete = await client.DeleteAsync($"/v1/projects/{projectId}/tasks/{otherId}", cancellationToken: TestContext.Current.CancellationToken);
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

        var labels = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/labels", TestContext.Current.CancellationToken);
        Assert.True(labels.GetArrayLength() >= 1);
        var match = await client.GetAsync($"/v1/projects/{projectId}/labels/match?path=ProjectBeacon.API/Program.cs", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(match.IsSuccessStatusCode, await match.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var matched = await match.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
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
        var got = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/reports/{reportId}", TestContext.Current.CancellationToken);
        Assert.Equal(reportId, got.GetProperty("id").GetGuid());

        var other = await PostJson(client, "/v1/projects", new { name = "Q", orgId });
        var otherId = other.GetProperty("id").GetGuid();
        var cross = await client.GetAsync($"/v1/projects/{otherId}/reports/{reportId}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        var reports = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/reports", TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task NonMember_CannotListForeignProjectTasks()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();
        await PostJson(client, $"/v1/projects/{projectId}/tasks", new
        {
            title = "secret",
            projectId,
            priority = 1,
            type = 3
        });

        var register = await client.PostAsJsonAsync("/v1/auth/register", new
        {
            login = "outsider",
            email = "outsider@beacon.local",
            password = "Passw0rd!x"
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(register.IsSuccessStatusCode, await register.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { login = "outsider", password = "Passw0rd!x" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var outsiderJwt = (await login.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken)).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderJwt);

        var listed = await client.GetAsync($"/v1/projects/{projectId}/tasks", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(listed.IsSuccessStatusCode, await listed.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var tasks = await listed.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        Assert.Equal(0, tasks.GetArrayLength());
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
