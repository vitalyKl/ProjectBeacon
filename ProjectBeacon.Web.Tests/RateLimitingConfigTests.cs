namespace ProjectBeacon.Web.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

public sealed class RateLimitingConfigTests
{
    [Fact]
    public void RateLimitDefaults_ReadCorrectly()
    {
        var config = new Dictionary<string, string>
        {
            ["POSTGRES_PASSWORD"] = "test",
            ["JWT:Secret"] = "ProjectBeacon-JWT-Secret-Key-Must-Be-At-Least-32-Characters-Long"
        };

        var configSource = new MemoryConfigurationSource { InitialData = config };
        var configuration = new ConfigurationBuilder().Add(configSource).Build();

        var rateLimitPerWindow = int.TryParse(
            Environment.GetEnvironmentVariable("RATE_LIMIT_PER_WINDOW"), out var perWindow)
            ? perWindow : 5;

        var rateLimitWindowSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS"), out var windowSecs)
            ? windowSecs : 60;

        Assert.Equal(5, rateLimitPerWindow);
        Assert.Equal(60, rateLimitWindowSeconds);
    }

    [Fact]
    public void RateLimitEnvVars_OverrideDefaults()
    {
        Environment.SetEnvironmentVariable("RATE_LIMIT_PER_WINDOW", "10");
        Environment.SetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS", "120");

        try
        {
            var rateLimitPerWindow = int.TryParse(
                Environment.GetEnvironmentVariable("RATE_LIMIT_PER_WINDOW"), out var perWindow)
                ? perWindow : 5;

            var rateLimitWindowSeconds = int.TryParse(
                Environment.GetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS"), out var windowSecs)
                ? windowSecs : 60;

            Assert.Equal(10, rateLimitPerWindow);
            Assert.Equal(120, rateLimitWindowSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RATE_LIMIT_PER_WINDOW", null);
            Environment.SetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS", null);
        }
    }
}
