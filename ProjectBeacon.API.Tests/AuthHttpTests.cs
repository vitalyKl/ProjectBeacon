namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Json;
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
        await client.PostAsJsonAsync("/v1/auth/bootstrap", new { });

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
}

public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public AuthApiFactory()
    {
        Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", "test");
        Environment.SetEnvironmentVariable("BOOTSTRAP_ADMIN_TOKEN", "");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
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
