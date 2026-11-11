namespace ProjectBeacon.Web.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public sealed class CookieLoginHttpTests
{
    [Fact]
    public async Task CookieLogin_SetsBeaconAuthAndRedirects()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var password = await WebTestFactory.GetBootstrapPasswordAsync(client);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password!
        });
        var response = await client.PostAsync("/v1/auth/cookie-login", form, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard", response.Headers.Location?.ToString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("BeaconAuth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrowserLogout_ClearsOnlyCurrentCookieAndKeepsJwtSessionValid()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var password = await WebTestFactory.GetBootstrapPasswordAsync(client);

        using var cookieLogin = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/cookie-login");
        cookieLogin.Headers.Host = "app.projectbeacon.tech";
        cookieLogin.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });
        var cookieLoginResponse = await client.SendAsync(
            cookieLogin, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, cookieLoginResponse.StatusCode);
        Assert.Contains(cookieLoginResponse.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("BeaconAuth=", StringComparison.Ordinal));

        using var jwtLogin = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/login");
        jwtLogin.Headers.Host = "app.projectbeacon.tech";
        jwtLogin.Content = System.Net.Http.Json.JsonContent.Create(new
        {
            Login = "admin",
            Password = password
        });
        var jwtLoginResponse = await client.SendAsync(jwtLogin, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, jwtLoginResponse.StatusCode);
        var jwtJson = await jwtLoginResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);
        var jwt = jwtJson.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jwt));

        using var logout = new HttpRequestMessage(HttpMethod.Get, "/logout");
        logout.Headers.Host = "app.projectbeacon.tech";
        var logoutResponse = await client.SendAsync(logout, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, logoutResponse.StatusCode);
        Assert.Equal("/login", logoutResponse.Headers.Location?.AbsolutePath);
        Assert.Contains(logoutResponse.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("BeaconAuth=", StringComparison.Ordinal)
                && value.Contains("expires=", StringComparison.OrdinalIgnoreCase));

        using var cookieCheck = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/me");
        cookieCheck.Headers.Host = "app.projectbeacon.tech";
        var cookieResponse = await client.SendAsync(cookieCheck, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, cookieResponse.StatusCode);

        using var jwtCheck = new HttpRequestMessage(HttpMethod.Get, "/v1/auth/me");
        jwtCheck.Headers.Host = "app.projectbeacon.tech";
        jwtCheck.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var jwtResponse = await client.SendAsync(jwtCheck, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, jwtResponse.StatusCode);
    }

    [Fact]
    public async Task Landing_Anonymous_Ok_NotPermanentRedirect()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task ControlRecoverPage_HasDedicatedRoute()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/control/recover",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RegisterForgotResetInvitePages_AccessibleToAnonymous()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/register", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/forgot", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reset", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/invite", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Culture_SetsCookie_AndRejectsUnknown()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var ru = await client.GetAsync("/culture?culture=ru&returnUrl=/dashboard", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, ru.StatusCode);
        Assert.Equal("/dashboard", ru.Headers.Location?.ToString());
        Assert.Contains(ru.Headers.GetValues("Set-Cookie"),
            c => c.Contains(".AspNetCore.Culture", StringComparison.Ordinal) && c.Contains("ru", StringComparison.OrdinalIgnoreCase));

        var bad = await client.GetAsync("/culture?culture=xx&returnUrl=/dashboard", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Contains(bad.Headers.GetValues("Set-Cookie"),
            c => c.Contains(".AspNetCore.Culture", StringComparison.Ordinal) && c.Contains("en", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CookieLogin_Production_PlainHttp_SetsSecureCookie()
    {
        // In Production the auth cookie must always carry Secure, even on plain HTTP.
        await using var factory = new WebProductionTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var password = await WebTestFactory.GetBootstrapPasswordAsync(client);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });
        var response = await client.PostAsync("/v1/auth/cookie-login", form, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var attributes = GetBeaconAuthCookieAttributes(response);
        Assert.Contains("Secure", attributes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CookieLogin_Production_SpoofedForwardedProtoHttp_SecureCookieNotStripped()
    {
        // A client or proxy cannot spoof X-Forwarded-Proto: http to strip Secure
        // from the Production auth cookie.
        await using var factory = new WebProductionTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var password = await WebTestFactory.GetBootstrapPasswordAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/cookie-login");
        request.Headers.Add("X-Forwarded-Proto", "http");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password
        });
        var response = await client.SendAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var attributes = GetBeaconAuthCookieAttributes(response);
        Assert.Contains("Secure", attributes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnonymousBoard_ChallengesToLogin()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/board", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized or HttpStatusCode.OK,
            response.StatusCode.ToString());
        if (response.StatusCode == HttpStatusCode.Redirect)
            Assert.Contains("/login", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
        else if (response.StatusCode == HttpStatusCode.OK)
            Assert.Contains("login", (await response.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken)).ToLowerInvariant());
    }

    private static string[] GetBeaconAuthCookieAttributes(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .FirstOrDefault(value => value.StartsWith("BeaconAuth=", StringComparison.Ordinal));
        Assert.NotNull(setCookie);
        var tail = setCookie.Contains(';') ? setCookie[(setCookie.IndexOf(';') + 1)..] : string.Empty;
        return tail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>
/// WebTestFactory running the host in the Production environment (with the same
/// in-memory SQLite database) to verify Production-only security behavior.
/// </summary>
public sealed class WebProductionTestFactory : WebTestFactory
{
    public WebProductionTestFactory()
    {
        Environment.SetEnvironmentVariable("BEACON_MIGRATE_ON_START", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // The last UseEnvironment call wins; Production is required for the
        // Production-only cookie and secret policies.
        builder.UseEnvironment("Production");
    }
}

public class WebTestFactory : WebApplicationFactory<ProjectBeacon.Web.Features.Dashboard.Dashboard>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public const string BootstrapToken = "web-test-bootstrap-token";
    public const string RecoveryToken = "web-test-admin-recovery-token";

    public WebTestFactory()
    {
        Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", "test");
        Environment.SetEnvironmentVariable("BOOTSTRAP_ADMIN_TOKEN", BootstrapToken);
        Environment.SetEnvironmentVariable("ADMIN_RECOVERY_TOKEN", RecoveryToken);
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("JWT:Secret", "ProjectBeacon-JWT-Secret-Key-Must-Be-At-Least-32-Characters-Long");
        // The Production environment requires an explicit data key; the legacy
        // v1 material is bridged from JWT:Secret, mirroring the rollout phase.
        builder.UseSetting("DATA_PROTECTION_KEY", "ProjectBeacon-Data-Protection-Key-Must-Be-At-Least-32-Chars");
        builder.UseSetting("BOOTSTRAP_ADMIN_TOKEN", BootstrapToken);
        builder.UseSetting("ADMIN_RECOVERY_TOKEN", RecoveryToken);
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.ToList())
            {
                var typeName = descriptor.ServiceType.FullName ?? string.Empty;
                var implName = descriptor.ImplementationType?.FullName ?? string.Empty;
                if (typeName.Contains("BeaconDbContext", StringComparison.Ordinal) ||
                    implName.Contains("BeaconDbContext", StringComparison.Ordinal) ||
                    typeName.Contains("EntityFrameworkCore", StringComparison.Ordinal) ||
                    implName.Contains("Npgsql", StringComparison.Ordinal))
                {
                    services.Remove(descriptor);
                }
            }

            services.AddDbContext<BeaconDbContext>(o => o.UseSqlite(_connection));
            services.AddScoped<IDbContextFactory<BeaconDbContext>, BeaconDbFactory>();
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<BeaconDbContext>().Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _connection.Dispose();
        base.Dispose(disposing);
    }

    public static async Task<string> GetBootstrapPasswordAsync(HttpClient client)
    {
        // Bootstrap is a Control Plane-only operation. The subsequent cookie-login
        // request in these tests intentionally uses the customer host.
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/bootstrap");
        request.Headers.Host = "control.projectbeacon.tech";
        request.Content = System.Net.Http.Json.JsonContent.Create(new { });
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BootstrapToken);
        var boot = await client.SendAsync(request);
        boot.EnsureSuccessStatusCode();
        var bootJson = await boot.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return bootJson.GetProperty("password").GetString()!;
    }
}
