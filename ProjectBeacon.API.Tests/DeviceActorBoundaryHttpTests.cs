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

        var created = await client.PostAsJsonAsync("/v1/devices", new { name = "bnd1", fingerprint = "fp-bnd1" });
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var token = device.GetProperty("token").GetString();

        var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await deviceClient.GetAsync("/v1/devices");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeviceToken_DeviceOnlyEndpoint_ReturnsSuccess()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var created = await client.PostAsJsonAsync("/v1/devices", new { name = "bnd2", fingerprint = "fp-bnd2" });
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var token = device.GetProperty("token").GetString();

        var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await deviceClient.PostAsJsonAsync("/v1/devices/me/heartbeat", new { probeJson = "{}", workstationJson = "{}" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HumanToken_DeviceOnlyEndpoint_Returns401()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await client.PostAsJsonAsync("/v1/devices/me/heartbeat", new { probeJson = "{}", workstationJson = "{}" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeviceToken_AuthMe_Returns403()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var created = await client.PostAsJsonAsync("/v1/devices", new { name = "bnd3", fingerprint = "fp-bnd3" });
        created.EnsureSuccessStatusCode();
        var device = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var token = device.GetProperty("token").GetString();

        var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await deviceClient.GetAsync("/v1/auth/me");
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
        return (await login.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
    }
}
