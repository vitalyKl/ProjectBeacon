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
        Assert.Equal(1, page.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());

        var export = await client.GetAsync("/v1/admin/audit/export", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        var csv = await export.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("AtUtc,ActorKind,ActorUserId", csv);
        Assert.Contains("account.login.succeeded", csv);

        var invalidPage = await client.GetAsync("/v1/admin/audit?pageSize=201", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);

        var orgResponse = await client.PostAsJsonAsync(
            "/v1/orgs",
            new { name = "Audit Denial Target" },
            cancellationToken: TestContext.Current.CancellationToken);
        orgResponse.EnsureSuccessStatusCode();
        var org = await orgResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var targetOrgId = org.GetProperty("id").GetGuid();

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
        var me = await client.GetFromJsonAsync<JsonElement>("/v1/auth/me", TestContext.Current.CancellationToken);
        var regularUserId = me.GetProperty("id").GetGuid();

        var forbidden = await client.GetAsync("/v1/admin/audit", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var forbiddenExport = await client.GetAsync("/v1/admin/audit/export", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenExport.StatusCode);

        var deniedCreate = await client.PostAsJsonAsync(
            "/v1/projects",
            new { name = "Unauthorized Project", orgId = targetOrgId },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, deniedCreate.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var deniedEventsResponse = await client.GetAsync(
            $"/v1/admin/audit?actorUserId={regularUserId}&action=authorization.denied&pageSize=100",
            TestContext.Current.CancellationToken);
        deniedEventsResponse.EnsureSuccessStatusCode();
        var deniedEvents = await deniedEventsResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains(deniedEvents.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("targetId").GetString() == "POST /v1/projects");
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
