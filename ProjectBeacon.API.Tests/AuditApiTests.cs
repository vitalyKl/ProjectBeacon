namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public sealed class AuditApiTests
{
    [Fact]
    public async Task PlatformAdmin_CanReadAndExportAudit_ButRegularUserCannot()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var bootstrap = await client.SendAsync(BootstrapRequest(AuthApiFactory.BootstrapToken),
            cancellationToken: TestContext.Current.CancellationToken);
        bootstrap.EnsureSuccessStatusCode();
        var bootJson = await bootstrap.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var adminPassword = bootJson.GetProperty("password").GetString()!;

        var adminLogin = await client.PostAsJsonAsync("/v1/auth/login",
            new { Login = "admin", Password = adminPassword },
            cancellationToken: TestContext.Current.CancellationToken);
        adminLogin.EnsureSuccessStatusCode();
        var adminJwt = (await adminLogin.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var page = await client.GetAsync("/v1/admin/audit", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var pageJson = await page.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(pageJson.GetProperty("totalCount").GetInt64() >= 1);

        var export = await client.GetAsync("/v1/admin/audit/export.csv", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.StartsWith("text/csv", export.Content.Headers.ContentType!.ToString(), StringComparison.OrdinalIgnoreCase);
        var csv = await export.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("platform.bootstrap.completed", csv);
        Assert.DoesNotContain(adminPassword, csv);

        var registration = await client.PostAsJsonAsync("/v1/auth/register", new
        {
            login = "audit-member",
            email = "audit-member@example.com",
            password = "password1"
        }, cancellationToken: TestContext.Current.CancellationToken);
        registration.EnsureSuccessStatusCode();

        var memberLogin = await client.PostAsJsonAsync("/v1/auth/login",
            new { Login = "audit-member", Password = "password1" },
            cancellationToken: TestContext.Current.CancellationToken);
        memberLogin.EnsureSuccessStatusCode();
        var memberJwt = (await memberLogin.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", memberJwt);

        var denied = await client.GetAsync("/v1/admin/audit", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        using (TenantScope.EnterUnscoped())
        {
            var deniedEvent = await db.AuditEvents
                .OrderByDescending(e => e.At)
                .FirstOrDefaultAsync(e => e.Action == "security.authorization.denied",
                    TestContext.Current.CancellationToken);
            Assert.NotNull(deniedEvent);
            Assert.Equal(Domain.Entities.Audit.AuditActorKind.Human, deniedEvent.ActorKind);
            Assert.Contains("/v1/admin/audit", deniedEvent.TargetId);
        }
    }

    private static HttpRequestMessage BootstrapRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/bootstrap")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}