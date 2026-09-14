namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

[Collection("api-http")]
public sealed class CoordinationHttpTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task DecisionsConstraintsMilestones_AndLabelPath()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapAndLogin(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await PostJson(client, "/v1/orgs", new { name = "Org" });
        var orgId = org.GetProperty("id").GetGuid();
        var project = await PostJson(client, "/v1/projects", new { name = "P", orgId });
        var projectId = project.GetProperty("id").GetGuid();

        var decision = await PostJson(client, $"/v1/projects/{projectId}/decisions", new
        {
            title = "Use Postgres",
            context = "need a db",
            body = "Postgres 16",
            consequences = "SQLite is not proof"
        });
        var decisionId = decision.GetProperty("id").GetGuid();
        Assert.Equal("Proposed", decision.GetProperty("status").GetString());

        var accepted = await client.PostAsJsonAsync($"/v1/projects/{projectId}/decisions/{decisionId}/accept", new { });
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        var listed = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/decisions");
        Assert.Equal("Accepted", listed[0].GetProperty("status").GetString());

        var constraint = await PostJson(client, $"/v1/projects/{projectId}/constraints", new
        {
            body = "tests must pass",
            kind = "Must"
        });
        var constraintId = constraint.GetProperty("id").GetGuid();
        Assert.Equal("Proposed", constraint.GetProperty("status").GetString());
        var activated = await client.PostAsJsonAsync($"/v1/projects/{projectId}/constraints/{constraintId}/activate", new { });
        Assert.True(activated.IsSuccessStatusCode, await activated.Content.ReadAsStringAsync());

        var milestone = await PostJson(client, $"/v1/projects/{projectId}/milestones", new
        {
            name = "M1",
            description = "first",
            projectId,
            order = 0
        });
        var milestoneId = milestone.GetProperty("id").GetGuid();
        var closed = await client.PostAsJsonAsync($"/v1/projects/{projectId}/milestones/{milestoneId}/close", new { });
        Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync());
        var closedJson = JsonSerializer.Deserialize<JsonElement>(await closed.Content.ReadAsStringAsync(), Json);
        Assert.False(closedJson.GetProperty("closedAt").ValueKind == JsonValueKind.Null);

        var labels = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/labels");
        var apiLabel = labels.EnumerateArray().First(l => l.GetProperty("name").GetString() == "API");
        var labelId = apiLabel.GetProperty("id").GetGuid();
        var path = await client.PostAsJsonAsync($"/v1/projects/{projectId}/labels/{labelId}/paths", new { path = "ProjectBeacon.API.Tests" });
        Assert.True(path.IsSuccessStatusCode, await path.Content.ReadAsStringAsync());
        var match = await client.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/labels/match?path=ProjectBeacon.API.Tests/Foo.cs");
        Assert.Equal(labelId, match.GetProperty("id").GetGuid());
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
