namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Infrastructure.Security;

[Collection("api-http")]
public sealed class AuditApiTests
{
    [Fact]
    public async Task PlatformAdmin_CanReadAndExportAudit_ButRegularUserCannot()
    {
        await using var factory = new AuditApiFactory();
        var client = factory.CreateClient();

        var deniedBootstrap = await client.SendAsync(BootstrapRequest("invalid-bootstrap-token"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedBootstrap.StatusCode);

        var bootstrap = await client.SendAsync(BootstrapRequest(AuthApiFactory.BootstrapToken),
            cancellationToken: TestContext.Current.CancellationToken);
        bootstrap.EnsureSuccessStatusCode();
        var bootJson = await bootstrap.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var adminId = bootJson.GetProperty("userId").GetGuid();
        var adminPassword = bootJson.GetProperty("password").GetString()!;

        var deniedRecovery = await client.SendAsync(
            RecoveryRequest("invalid-recovery-token"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedRecovery.StatusCode);

        var adminLogin = await client.PostAsJsonAsync("/v1/auth/login",
            new { Login = "admin", Password = adminPassword },
            cancellationToken: TestContext.Current.CancellationToken);
        adminLogin.EnsureSuccessStatusCode();
        var adminJwt = (await adminLogin.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);

        var failedLogin = await client.PostAsJsonAsync("/v1/auth/login",
            new { Login = "admin", Password = "wrong-password" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, failedLogin.StatusCode);

        var page = await client.GetAsync("/v1/admin/audit", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var pageJson = await page.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(pageJson.GetProperty("totalCount").GetInt64() >= 3);

        using (TenantScope.EnterUnscoped())
        {
            using var exportScope = factory.Services.CreateScope();
            var exportDb = exportScope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            exportDb.AuditEvents.AddRange(
                AuditEvent.Create(
                    DateTime.UtcNow,
                    AuditActorKind.System,
                    null,
                    null,
                    null,
                    "audit.export.formula_test",
                    "Test",
                    "formula",
                    null,
                    null,
                    AuditOutcome.Success,
                    userAgent: "  =HYPERLINK(\"https://example.invalid\",\"open\")"),
                AuditEvent.Create(
                    DateTime.UtcNow,
                    AuditActorKind.ApiToken,
                    null,
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    null,
                    "audit.filter.token",
                    "ApiToken",
                    "11111111-1111-1111-1111-111111111111",
                    null,
                    null,
                    AuditOutcome.Success),
                AuditEvent.Create(
                    DateTime.UtcNow,
                    AuditActorKind.Device,
                    null,
                    null,
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "audit.filter.device",
                    "Device",
                    "22222222-2222-2222-2222-222222222222",
                    null,
                    null,
                    AuditOutcome.Success),
                AuditEvent.Create(
                    DateTime.UtcNow,
                    AuditActorKind.System,
                    null,
                    null,
                    null,
                    "audit.filter.trace",
                    "Trace",
                    "trace-control-plane-9876",
                    null,
                    null,
                    AuditOutcome.Success,
                    traceId: "trace-control-plane-9876"));
            await exportDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var export = await client.GetAsync("/v1/admin/audit/export.csv", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        var csv = await export.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("platform.bootstrap.completed", csv);
        Assert.Contains("platform.bootstrap.denied", csv);
        Assert.Contains("platform.admin_recovery.denied", csv);
        Assert.Contains("account.login.succeeded", csv);
        Assert.Contains("account.login.denied", csv);
        Assert.Contains("'  =HYPERLINK(", csv);
        Assert.DoesNotContain(adminPassword, csv);
        Assert.DoesNotContain(AuthApiFactory.BootstrapToken, csv);

        var tokenFilter = await client.GetFromJsonAsync<JsonElement>(
            "/v1/admin/audit?actorTokenId=11111111-1111-1111-1111-111111111111",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, tokenFilter.GetProperty("items").GetArrayLength());
        var deviceFilter = await client.GetFromJsonAsync<JsonElement>(
            "/v1/admin/audit?actorDeviceId=22222222-2222-2222-2222-222222222222",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, deviceFilter.GetProperty("items").GetArrayLength());
        var traceFilter = await client.GetFromJsonAsync<JsonElement>(
            "/v1/admin/audit?traceId=trace-control-plane-9876",
            TestContext.Current.CancellationToken);
        Assert.Equal(1, traceFilter.GetProperty("items").GetArrayLength());
        var traceExport = await client.GetStringAsync(
            "/v1/admin/audit/export.csv?traceId=trace-control-plane-9876",
            TestContext.Current.CancellationToken);
        Assert.Contains("audit.filter.trace", traceExport);
        Assert.DoesNotContain("audit.filter.device", traceExport);

        var member = User.Create(
            "audit-member",
            "audit-member@example.com",
            PasswordHasher.Hash("password1"));
        using (TenantScope.EnterUnscoped())
        {
            using var seedScope = factory.Services.CreateScope();
            var seedDb = seedScope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            seedDb.Users.Add(member);
            await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

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

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminJwt);
        var deniedFilter = await client.GetFromJsonAsync<JsonElement>(
            "/v1/admin/audit?action=security.authorization.denied&targetType=HttpEndpoint&targetId="
                + Uri.EscapeDataString("GET /v1/admin/audit"),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, deniedFilter.GetProperty("items").GetArrayLength());

        var logout = await client.PostAsync("/v1/auth/logout", content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        using (TenantScope.EnterUnscoped())
        {
            using var auditScope = factory.Services.CreateScope();
            var auditDb = auditScope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            var logoutEvent = await auditDb.AuditEvents.SingleAsync(
                item => item.Action == "account.logout" && item.ActorUserId == adminId,
                TestContext.Current.CancellationToken);
            Assert.Equal(AuditActorKind.Human, logoutEvent.ActorKind);
            Assert.Equal(adminId.ToString(), logoutEvent.TargetId);
        }

        var recovered = await client.SendAsync(
            RecoveryRequest(AuthApiFactory.BootstrapToken),
            cancellationToken: TestContext.Current.CancellationToken);
        recovered.EnsureSuccessStatusCode();
        var recoveredJson = await recovered.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(adminId, recoveredJson.GetProperty("userId").GetGuid());
        var recoveredPassword = recoveredJson.GetProperty("password").GetString()!;
        Assert.NotEqual(adminPassword, recoveredPassword);

        using (TenantScope.EnterUnscoped())
        {
            using var auditScope = factory.Services.CreateScope();
            var auditDb = auditScope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            var deniedRecoveryEvent = await auditDb.AuditEvents.SingleAsync(
                item => item.Action == "platform.admin_recovery.denied",
                TestContext.Current.CancellationToken);
            Assert.Equal(AuditOutcome.Denied, deniedRecoveryEvent.Outcome);
            Assert.Equal(AuditActorKind.System, deniedRecoveryEvent.ActorKind);
            Assert.Contains("bootstrap_token_invalid", deniedRecoveryEvent.DetailsJson);

            var recoveryEvent = await auditDb.AuditEvents.SingleAsync(
                item => item.Action == "platform.admin_recovery.completed",
                TestContext.Current.CancellationToken);
            Assert.Equal(AuditOutcome.Success, recoveryEvent.Outcome);
            Assert.Equal("User", recoveryEvent.TargetType);
            Assert.Equal(adminId.ToString(), recoveryEvent.TargetId);
            Assert.Contains("password_reset", recoveryEvent.DetailsJson);
            Assert.DoesNotContain(recoveredPassword, recoveryEvent.DetailsJson ?? string.Empty);
            Assert.DoesNotContain(AuthApiFactory.BootstrapToken, recoveryEvent.DetailsJson ?? string.Empty);
            Assert.DoesNotContain("password_reset", deniedRecoveryEvent.DetailsJson);
            Assert.DoesNotContain(AuthApiFactory.BootstrapToken, deniedRecoveryEvent.DetailsJson ?? string.Empty);
        }

        var recoveredLogin = await client.PostAsJsonAsync("/v1/auth/login",
            new { Login = "admin", Password = recoveredPassword },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, recoveredLogin.StatusCode);
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

    private static HttpRequestMessage RecoveryRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/recover-admin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}


/// <summary>
/// The audit lifecycle scenario intentionally performs more than five auth operations.
/// Rate limiting is covered independently by AuthHttpTests; keep this test's auth budget
/// high and serialize it with those tests because the host reads limits from process env.
/// </summary>
public sealed class AuditApiFactory : AuthApiFactory
{
    private readonly string? _previousAuthLimit;

    public AuditApiFactory()
    {
        _previousAuthLimit = Environment.GetEnvironmentVariable("RATE_LIMIT_PER_WINDOW");
        Environment.SetEnvironmentVariable("RATE_LIMIT_PER_WINDOW", "20");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Environment.SetEnvironmentVariable("RATE_LIMIT_PER_WINDOW", _previousAuthLimit);
        base.Dispose(disposing);
    }
}
