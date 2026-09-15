namespace ProjectBeacon.Web.Tests;

using System.Net;
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

        var boot = await client.PostAsJsonAsync("/v1/auth/bootstrap", new { });
        boot.EnsureSuccessStatusCode();
        var bootJson = await boot.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var password = bootJson.GetProperty("password").GetString();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password!
        });
        var response = await client.PostAsync("/v1/auth/cookie-login", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard", response.Headers.Location?.ToString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("BeaconAuth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoverPage_AccessibleToAnonymous()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/recover");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Culture_SetsCookie_AndRejectsUnknown()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var ru = await client.GetAsync("/culture?culture=ru&returnUrl=/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, ru.StatusCode);
        Assert.Equal("/dashboard", ru.Headers.Location?.ToString());
        Assert.Contains(ru.Headers.GetValues("Set-Cookie"),
            c => c.Contains(".AspNetCore.Culture", StringComparison.Ordinal) && c.Contains("ru", StringComparison.OrdinalIgnoreCase));

        var bad = await client.GetAsync("/culture?culture=xx&returnUrl=/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Contains(bad.Headers.GetValues("Set-Cookie"),
            c => c.Contains(".AspNetCore.Culture", StringComparison.Ordinal) && c.Contains("en", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnonymousBoard_ChallengesToLogin()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/board");
        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized or HttpStatusCode.OK,
            response.StatusCode.ToString());
        if (response.StatusCode == HttpStatusCode.Redirect)
            Assert.Contains("/login", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
        else if (response.StatusCode == HttpStatusCode.OK)
            Assert.Contains("login", (await response.Content.ReadAsStringAsync()).ToLowerInvariant());
    }
}

public sealed class WebTestFactory : WebApplicationFactory<ProjectBeacon.Web.Features.Dashboard.Dashboard>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public WebTestFactory()
    {
        Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", "test");
        Environment.SetEnvironmentVariable("BOOTSTRAP_ADMIN_TOKEN", "");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("JWT:Secret", "ProjectBeacon-JWT-Secret-Key-Must-Be-At-Least-32-Characters-Long");
        builder.UseSetting("BOOTSTRAP_ADMIN_TOKEN", "");
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
}
