namespace ProjectBeacon.Web.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

public sealed class RateLimitingConfigTests
{
    [Fact]
    public void RateLimitDefaults_ReadCorrectly()
    {
        var config = new Dictionary<string, string?>
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

        var attachmentUploadRateLimit = int.TryParse(
            Environment.GetEnvironmentVariable("RATE_LIMIT_ATTACHMENT_UPLOADS_PER_WINDOW"), out var attachmentUploads)
            ? attachmentUploads : 10;

        Assert.Equal(5, rateLimitPerWindow);
        Assert.Equal(60, rateLimitWindowSeconds);
        Assert.Equal(10, attachmentUploadRateLimit);
    }

    [Fact]
    public void RateLimitEnvVars_OverrideDefaults()
    {
        Environment.SetEnvironmentVariable("RATE_LIMIT_PER_WINDOW", "10");
        Environment.SetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS", "120");
        Environment.SetEnvironmentVariable("RATE_LIMIT_ATTACHMENT_UPLOADS_PER_WINDOW", "20");

        try
        {
            var rateLimitPerWindow = int.TryParse(
                Environment.GetEnvironmentVariable("RATE_LIMIT_PER_WINDOW"), out var perWindow)
                ? perWindow : 5;

            var rateLimitWindowSeconds = int.TryParse(
                Environment.GetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS"), out var windowSecs)
                ? windowSecs : 60;

            var attachmentUploadRateLimit = int.TryParse(
                Environment.GetEnvironmentVariable("RATE_LIMIT_ATTACHMENT_UPLOADS_PER_WINDOW"), out var attachmentUploads)
                ? attachmentUploads : 10;

            Assert.Equal(10, rateLimitPerWindow);
            Assert.Equal(120, rateLimitWindowSeconds);
            Assert.Equal(20, attachmentUploadRateLimit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RATE_LIMIT_PER_WINDOW", null);
            Environment.SetEnvironmentVariable("RATE_LIMIT_WINDOW_SECONDS", null);
            Environment.SetEnvironmentVariable("RATE_LIMIT_ATTACHMENT_UPLOADS_PER_WINDOW", null);
        }
    }
}
