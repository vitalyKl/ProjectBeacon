namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

[Collection("api-http")]
public sealed class AuditHttpTests
{
    [Fact]
    public async Task AuditHistory_RequiresAuthenticationAndVerifiedOperator()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/v1/admin/audit", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var bootstrapPassword = await Bootstrap(client);
        var adminToken = await Login(client, "admin", bootstrapPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var audit = await client.GetAsync("/v1/admin/audit", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var page = await audit.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(50, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, page.GetProperty("totalCount").GetInt64());
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());

        var invalidPage = await client.GetAsync("/v1/admin/audit?pageSize=201", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        var registered = await client.PostAsJsonAsync("/v1/auth/register", new
        {
            login = "regular-user",
            email = "regular-user@example.test",
            password = "password123"
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);

        var regularToken = await Login(client, "regular-user", "password123");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", regularToken);
        var forbidden = await client.GetAsync("/v1/admin/audit", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private static async Task<string> Bootstrap(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/bootstrap")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AuthApiFactory.BootstrapToken);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("password").GetString()!;
    }

    private static async Task<string> Login(HttpClient client, string login, string password)
    {
        var response = await client.PostAsJsonAsync("/v1/auth/login",
            new { login, password }, cancellationToken: TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("token").GetString()!;
    }
}
