namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

[Collection("api-http")]
public sealed class DeviceActorBoundaryHttpTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task DeviceToken_HumanEndpoint_Returns403()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var created = await client.PostAsJsonAsync("/v1/devices", new { name = "bnd1", fingerprint = "fp-bnd1" }, cancellationToken: TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        var token = device.GetProperty("token").GetString();

        var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await deviceClient.GetAsync("/v1/devices", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeviceToken_DeviceOnlyEndpoint_ReturnsSuccess()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var created = await client.PostAsJsonAsync("/v1/devices", new { name = "bnd2", fingerprint = "fp-bnd2" }, cancellationToken: TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        var token = device.GetProperty("token").GetString();

        var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await deviceClient.PostAsJsonAsync("/v1/devices/me/heartbeat", new { probeJson = "{}", workstationJson = "{}" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HumanToken_DeviceOnlyEndpoint_Returns401()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await client.PostAsJsonAsync("/v1/devices/me/heartbeat", new { probeJson = "{}", workstationJson = "{}" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceToken_AuthMe_Returns403()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var created = await client.PostAsJsonAsync("/v1/devices", new { name = "bnd3", fingerprint = "fp-bnd3" }, cancellationToken: TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        var token = device.GetProperty("token").GetString();

        var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await deviceClient.GetAsync("/v1/auth/me", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeviceToken_AutoSyncTargetsDeviceOnlyEndpoint_ReturnsSuccess()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var created = await client.PostAsJsonAsync(
            "/v1/devices", new { name = "autosync-device", fingerprint = "fp-autosync-device" },
            cancellationToken: TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        var token = device.GetProperty("token").GetString();

        var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await deviceClient.GetAsync(
            "/v1/devices/me/auto-sync-targets", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var targets = await response.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Array, targets.ValueKind);
    }

    [Fact]
    public async Task HumanToken_AutoSyncTargetsDeviceOnlyEndpoint_Returns401()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await client.GetAsync(
            "/v1/devices/me/auto-sync-targets", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceToken_RemoteLandingReconciliation_IsDeviceOnlyAndReachesScopedHandler()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var created = await client.PostAsJsonAsync(
            "/v1/devices", new { name = "remote-landing-device", fingerprint = "fp-remote-landing" },
            cancellationToken: TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        var token = device.GetProperty("token").GetString();

        var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var heartbeat = await deviceClient.PostAsJsonAsync(
            "/v1/devices/me/heartbeat",
            new
            {
                probeJson = """{"capabilities":["git-sync-v1","git-auto-sync-v1"]}""",
                workstationJson = "{}"
            },
            cancellationToken: TestContext.Current.CancellationToken);
        heartbeat.EnsureSuccessStatusCode();

        var response = await deviceClient.PostAsJsonAsync(
            "/v1/devices/me/remote-landings/reconcile",
            new
            {
                projectId = Guid.NewGuid(),
                taskId = Guid.NewGuid(),
                reviewRunId = Guid.NewGuid(),
                repository = "acme/project",
                pullRequestNumber = 42,
                pullRequestUrl = "https://github.com/acme/project/pull/42",
                sourceBranch = "beacon/task/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                targetBranch = "main",
                sourceSha = new string('a', 40),
                mergeCommitSha = new string('b', 40),
                remoteBaseSha = new string('c', 40),
                mergedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                verifiedAtUtc = DateTime.UtcNow
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HumanToken_RemoteLandingReconciliation_Returns401()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await client.PostAsJsonAsync(
            "/v1/devices/me/remote-landings/reconcile",
            new { },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
        return (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
    }
}
