namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[Collection("api-http")]
public sealed class AuthHttpTests
{
    [Fact]
    public async Task Login_WrongPassword_Unauthorized()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var bootstrap = await client.SendAsync(BootstrapRequest(AuthApiFactory.BootstrapToken));
        bootstrap.EnsureSuccessStatusCode();

        var fail = await client.PostAsJsonAsync("/v1/auth/login", new { Login = "admin", Password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, fail.StatusCode);
    }

    [Fact]
    public async Task AuthEndpoints_RateLimited()
    {
        Environment.SetEnvironmentVariable("RATE_LIMIT_PER_WINDOW", "5");
        Environment.SetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS", "60");
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
            last = await client.PostAsJsonAsync("/v1/auth/login", new { Login = "nobody", Password = "x" });

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    [Fact]
    public async Task Version_Anonymous_Ok()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/v1/version");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var version = json.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Contains('.', version);
    }

    [Fact]
    public async Task Register_Open_CreatesUser()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/auth/register", new
        {
            login = "bob",
            email = "bob@test.com",
            password = "password1"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_AlwaysOk()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/auth/forgot-password", new { email = "nobody@test.com" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProjectInvite_CreateAndAcceptViaRegister()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var jwt = await BootstrapJwt(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var org = await client.PostAsJsonAsync("/v1/orgs", new { name = "Org" });
        org.EnsureSuccessStatusCode();
        var orgJson = await org.Content.ReadFromJsonAsync<JsonElement>();
        var orgId = orgJson.GetProperty("id").GetGuid();
        var project = await client.PostAsJsonAsync("/v1/projects", new { name = "P", orgId });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var invite = await client.PostAsJsonAsync($"/v1/projects/{projectId}/invites", new
        {
            email = "bob@test.com",
            role = "Member"
        });
        invite.EnsureSuccessStatusCode();
        var inviteJson = await invite.Content.ReadFromJsonAsync<JsonElement>();
        var token = inviteJson.GetProperty("token").GetString();
        Assert.StartsWith("bci_", token);

        client.DefaultRequestHeaders.Authorization = null;
        var preview = await client.GetAsync($"/v1/invites/{token}");
        preview.EnsureSuccessStatusCode();

        var registered = await client.PostAsJsonAsync("/v1/auth/register", new
        {
            login = "bob",
            email = "bob@test.com",
            password = "password1",
            inviteToken = token
        });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
    }

    private static async Task<string> BootstrapJwt(HttpClient client)
    {
        var bootstrap = await client.SendAsync(BootstrapRequest(AuthApiFactory.BootstrapToken));
        bootstrap.EnsureSuccessStatusCode();
        var bootJson = await bootstrap.Content.ReadFromJsonAsync<JsonElement>();
        var password = bootJson.GetProperty("password").GetString();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { Login = "admin", Password = password });
        login.EnsureSuccessStatusCode();
        var json = await login.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task ApiHost_V1RequiresAuth_AndAssemblyIsApi()
    {
        Assert.Equal("ProjectBeacon.API", typeof(Program).Assembly.GetName().Name);
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var health = await client.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode, await health.Content.ReadAsStringAsync());
        var orgs = await client.GetAsync("/v1/orgs");
        Assert.Equal(HttpStatusCode.Unauthorized, orgs.StatusCode);
    }

    [Fact]
    public async Task RecoverAdmin_ValidToken_ResetsPassword()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var bootstrap = await client.SendAsync(BootstrapRequest(AuthApiFactory.BootstrapToken));
        bootstrap.EnsureSuccessStatusCode();
        var bootJson = await bootstrap.Content.ReadFromJsonAsync<JsonElement>();
        var oldPassword = bootJson.GetProperty("password").GetString()!;

        var recover = await client.SendAsync(RecoverRequest(AuthApiFactory.BootstrapToken));
        recover.EnsureSuccessStatusCode();
        var recoverJson = await recover.Content.ReadFromJsonAsync<JsonElement>();
        var newPassword = recoverJson.GetProperty("password").GetString()!;
        Assert.NotEqual(oldPassword, newPassword);

        var oldLogin = await client.PostAsJsonAsync("/v1/auth/login", new { Login = "admin", Password = oldPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await client.PostAsJsonAsync("/v1/auth/login", new { Login = "admin", Password = newPassword });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task RecoverAdmin_InvalidToken_Unauthorized()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var bootstrap = await client.SendAsync(BootstrapRequest(AuthApiFactory.BootstrapToken));
        bootstrap.EnsureSuccessStatusCode();

        var response = await client.SendAsync(RecoverRequest("wrong-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RecoverAdmin_NoAdmin_BadRequest()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var response = await client.SendAsync(RecoverRequest(AuthApiFactory.BootstrapToken));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RecoverAdmin_TokenNotConfigured_Unauthorized()
    {
        await using var factory = new AuthApiFactory(false);
        var client = factory.CreateClient();
        var response = await client.SendAsync(RecoverRequest(AuthApiFactory.BootstrapToken));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static HttpRequestMessage BootstrapRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/bootstrap");
        request.Content = JsonContent.Create(new { });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage RecoverRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/recover-admin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}

public class AuthApiFactory : WebApplicationFactory<Program>
{
    public const string BootstrapToken = "test-bootstrap-token";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly bool _configureBootstrapToken;

    public AuthApiFactory(bool configureBootstrapToken = true)
    {
        _configureBootstrapToken = configureBootstrapToken;
        Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", "test");
        Environment.SetEnvironmentVariable("BOOTSTRAP_ADMIN_TOKEN", _configureBootstrapToken ? BootstrapToken : "");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("JWT:Secret", "ProjectBeacon-JWT-Secret-Key-Must-Be-At-Least-32-Characters-Long");
        builder.UseSetting("BOOTSTRAP_ADMIN_TOKEN", _configureBootstrapToken ? BootstrapToken : "");
        builder.UseSetting("AUTH_LOCAL_INVITE_ONLY", "false");
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
