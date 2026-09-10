namespace ProjectBeacon.Infrastructure.Tests;

using Infrastructure.Data;
using Microsoft.Extensions.Configuration;

public sealed class PostgresConnectionTests
{
    [Fact]
    public void Resolve_Development_AppendsDefaultPassword()
    {
        var previousEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", null);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = "Host=localhost;Port=5432;Database=beacon;Username=beacon;"
                })
                .Build();

            var connectionString = PostgresConnection.Resolve(configuration);

            Assert.Contains("Password=beacon", connectionString, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnv);
            Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", previousPassword);
        }
    }

    [Fact]
    public void Resolve_UsesPostgresPasswordEnv()
    {
        var previousEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", "from-env");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = "Host=localhost;Port=5432;Database=beacon;Username=beacon;"
                })
                .Build();

            var connectionString = PostgresConnection.Resolve(configuration);
            Assert.Contains("Password=from-env", connectionString, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnv);
            Environment.SetEnvironmentVariable("POSTGRES_PASSWORD", previousPassword);
        }
    }
}
