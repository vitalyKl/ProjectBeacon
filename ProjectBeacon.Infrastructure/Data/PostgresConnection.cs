namespace ProjectBeacon.Infrastructure.Data;

using Microsoft.Extensions.Configuration;

public static class PostgresConnection
{
    public static string Resolve(IConfiguration configuration)
    {
        var fromConfig = configuration.GetConnectionString("Default");
        var password = FirstNonEmpty(
            Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"),
            configuration["POSTGRES_PASSWORD"]);

        if (string.IsNullOrWhiteSpace(password) && IsDevelopment())
            password = "beacon";

        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            if (HasPassword(fromConfig))
                return fromConfig;
            if (!string.IsNullOrWhiteSpace(password))
                return fromConfig.TrimEnd(';') + ";Password=" + password;
        }

        if (!string.IsNullOrWhiteSpace(password))
            return $"Host=localhost;Port=5432;Database=beacon;Username=beacon;Password={password}";

        throw new InvalidOperationException(
            "Set ConnectionStrings:Default (with Password) or POSTGRES_PASSWORD. For local runs, put them in .env at the repo root.");
    }

    private static bool IsDevelopment()
    {
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        return string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool HasPassword(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("Password=", StringComparison.OrdinalIgnoreCase) &&
                part.Length > "Password=".Length)
                return true;
        }

        return false;
    }
}
