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
        var raw = token.GetProperty("token").GetString();
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
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task ContextReadToken_CannotReadTaskContentHistory()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "History Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "History Project", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var section = await PostJson(client, $"/v1/projects/{projectId}/context/nodes", new
        {
            title = "Goals",
            bodyMarkdown = "Context that this token may read",
            sectionId = "goals",
            scopeType = 0,
            source = 0
        });
        var sectionId = section.GetProperty("id").GetGuid();

        var task = await PostJson(client, $"/v1/projects/{projectId}/tasks", new
        {
            title = "Private task",
            description = "Task description must stay behind TaskRead",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = task.GetProperty("id").GetGuid();

        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "context-only",
            capabilities = 8
        });
        var raw = token.GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);

        var contextHistory = await client.GetAsync(
            $"/v1/projects/{projectId}/content-versions?targetKind=0&targetId={sectionId}",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, contextHistory.StatusCode);

        var taskHistoryThroughContext = await client.GetAsync(
            $"/v1/projects/{projectId}/content-versions?targetKind=5&targetId={taskId}",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, taskHistoryThroughContext.StatusCode);

        var taskHistoryThroughTaskRoute = await client.GetAsync(
            $"/v1/projects/{projectId}/tasks/{taskId}/content-versions",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, taskHistoryThroughTaskRoute.StatusCode);

        var taskRestoreWithoutTaskWrite = await client.PostAsJsonAsync(
            $"/v1/projects/{projectId}/tasks/{taskId}/content-versions/{Guid.NewGuid()}/restore",
            new { expectedRevision = 1 },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, taskRestoreWithoutTaskWrite.StatusCode);
    }

    [Fact]
    public async Task TaskContentHistory_CanRestoreOnlyTheRoutedTask()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Task Restore Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "Task Restore Project", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var task = await PostJson(client, $"/v1/projects/{projectId}/tasks", new
        {
            title = "Restorable task",
            description = "Original description",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = task.GetProperty("id").GetGuid();
        Assert.Equal(1, task.GetProperty("contentRevision").GetInt32());

        Guid originalVersionId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            using (TenantScope.EnterUnscoped())
            {
                originalVersionId = await db.ContentVersions
                    .Where(version => version.ProjectId == projectId
                        && version.TargetKind == ContentTargetKind.TaskDescription
                        && version.TargetId == taskId
                        && version.Revision == 1)
                    .Select(version => version.Id)
                    .SingleAsync(TestContext.Current.CancellationToken);
            }
        }

        var update = await client.PutAsJsonAsync(
            $"/v1/projects/{projectId}/tasks/{taskId}",
            new
            {
                taskId,
                title = "Restorable task",
                description = "Updated description",
                priority = 1,
                type = 3,
                expectedContentRevision = 1
            },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        Assert.Equal(2, updated.GetProperty("contentRevision").GetInt32());

        var genericRestore = await client.PostAsJsonAsync(
            $"/v1/projects/{projectId}/content-versions/{originalVersionId}/restore",
            new { expectedRevision = 2 },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, genericRestore.StatusCode);

        var restore = await client.PostAsJsonAsync(
            $"/v1/projects/{projectId}/tasks/{taskId}/content-versions/{originalVersionId}/restore",
            new { expectedRevision = 2 },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        var restored = await client.GetFromJsonAsync<JsonElement>(
            $"/v1/projects/{projectId}/tasks/{taskId}",
            TestContext.Current.CancellationToken);
        Assert.Equal("Original description", restored.GetProperty("description").GetString());
        Assert.Equal(3, restored.GetProperty("contentRevision").GetInt32());

        var otherTask = await PostJson(client, $"/v1/projects/{projectId}/tasks", new
        {
            title = "Other task",
            description = "Other description",
            projectId,
            priority = 1,
            type = 3
        });
        var otherTaskId = otherTask.GetProperty("id").GetGuid();
        var wrongTarget = await client.PostAsJsonAsync(
            $"/v1/projects/{projectId}/tasks/{otherTaskId}/content-versions/{originalVersionId}/restore",
            new { expectedRevision = 1 },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, wrongTarget.StatusCode);
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

        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me", TestContext.Current.CancellationToken);
        var actorId = me.GetProperty("id").GetGuid().ToString();

        var missing = await client.PostAsJsonAsync("/v1/work/finish_work", new
        {
            taskId = taskId.ToString(),
            result = "done",
            output = "no review",
            actorId
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var selfReported = await client.PostAsJsonAsync("/v1/work/finish_work", new
        {
            taskId = taskId.ToString(),
            result = "done",
            output = "reviewed",
            actorId,
            review = new { reviewerRun = true, regressionsFound = 0, regressionsFixed = 0 }
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, selfReported.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            using (TenantScope.EnterUnscoped())
            {
                db.ReviewRuns.Add(ReviewRun.Create(projectId, taskId, "transcript-abc"));
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }

        var transcriptOnly = await client.PostAsJsonAsync("/v1/work/finish_work", new
        {
            taskId = taskId.ToString(),
            result = "done",
            output = "reviewed",
            actorId,
            review = new { reviewerRun = true, regressionsFound = 0, regressionsFixed = 0 },
            reviewTranscriptRef = "transcript-abc"
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, transcriptOnly.StatusCode);

        Guid proofId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            using (TenantScope.EnterUnscoped())
            {
                var proof = ReviewRun.Start(projectId, taskId, Domain.Enums.ReviewerType.Agent, Guid.NewGuid());
                proof.Complete("checked", "check:exit0");
                proofId = proof.Id;
                db.ReviewRuns.Add(proof);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }

        var ok = await client.PostAsJsonAsync("/v1/work/finish_work", new
        {
            taskId = taskId.ToString(),
            result = "done",
            output = "reviewed",
            actorId,
            review = new { reviewerRun = true, regressionsFound = 0, regressionsFixed = 0 },
            reviewRunId = proofId
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            using (TenantScope.EnterUnscoped())
            {
                var entity = await db.Tasks.FindAsync([taskId], cancellationToken: TestContext.Current.CancellationToken);
                Assert.NotNull(entity);
                Assert.Equal(Domain.Enums.TaskItemStatus.Done, entity.Status);
            }
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
