namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

[Collection("api-http")]
public sealed class DeviceHttpTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Enroll_Heartbeat_Command_Complete()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var created = await client.PostAsJsonAsync("/v1/devices", new { name = "laptop", fingerprint = "fp-http" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var device = await created.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        var deviceId = device.GetProperty("id").GetGuid();
        var token = device.GetProperty("token").GetString();
        Assert.StartsWith("bcd_", token);

        var listed = await client.GetFromJsonAsync<JsonElement>("/v1/devices", TestContext.Current.CancellationToken);
        Assert.True(listed.GetArrayLength() >= 1);
        Assert.False(listed[0].TryGetProperty("token", out var listedToken) && listedToken.ValueKind == JsonValueKind.String && listedToken.GetString()?.StartsWith("bcd_") == true && listedToken.GetString()!.Length > 12);

        var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var beat = await deviceClient.PostAsJsonAsync("/v1/devices/me/heartbeat", new { probeJson = "{\"git\":\"git\"}", workstationJson = "{}" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(beat.IsSuccessStatusCode, await beat.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));

        var enqueue = await client.PostAsJsonAsync($"/v1/devices/{deviceId}/commands", new { kind = "Probe", payloadJson = "{}" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        var command = await enqueue.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        var commandId = command.GetProperty("id").GetGuid();

        var claimed = await deviceClient.GetAsync("/v1/devices/me/commands?wait=0", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(claimed.IsSuccessStatusCode, await claimed.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
        var claimedBody = await claimed.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        Assert.Equal(commandId, claimedBody.GetProperty("id").GetGuid());

        var complete = await deviceClient.PostAsJsonAsync($"/v1/commands/{commandId}/complete", new
        {
            success = true,
            resultJson = "{\"ok\":true}"
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));

        var got = await client.GetFromJsonAsync<JsonElement>($"/v1/commands/{commandId}", TestContext.Current.CancellationToken);
        Assert.Equal("Succeeded", got.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Enqueue_WhenOffline_Conflict()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var created = await client.PostAsJsonAsync("/v1/devices", new { name = "off", fingerprint = "fp-off" }, cancellationToken: TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var deviceId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
        var enqueue = await client.PostAsJsonAsync($"/v1/devices/{deviceId}/commands", new { kind = "Probe" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, enqueue.StatusCode);
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
