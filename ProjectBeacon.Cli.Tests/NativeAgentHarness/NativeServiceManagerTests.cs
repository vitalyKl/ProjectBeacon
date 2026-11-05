namespace ProjectBeacon.Cli.Tests.NativeAgentHarness;

using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class NativeServiceManagerTests
{
    [Fact]
    public void ExposesStartStatusLogsAndStopTools()
    {
        var tools = new[]
        {
            NativeServiceManager.StartToolDefinition(),
            NativeServiceManager.StatusToolDefinition(),
            NativeServiceManager.LogsToolDefinition(),
            NativeServiceManager.StopToolDefinition()
        };

        Assert.Equal(
            ["service_start", "service_status", "service_logs", "service_stop"],
            tools.Select(tool => tool["function"]?["name"]?.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task StatusReturnsAnEmptyListForANewSession()
    {
        await using var manager = new NativeServiceManager();
        var result = manager.Status(Guid.NewGuid().ToString("N"), new JsonObject());

        Assert.False(result.IsError);
        Assert.Contains("\"services\":[]", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceStartRejectsBlockedExecutablesBeforeLaunching()
    {
        await using var manager = new NativeServiceManager();
        var root = Path.Combine(Path.GetTempPath(), "beacon-service-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await manager.StartAsync(
                Guid.NewGuid().ToString("N"),
                root,
                new JsonObject
                {
                    ["argv"] = new JsonArray
                    {
                        JsonValue.Create("bash"),
                        JsonValue.Create("-c"),
                        JsonValue.Create("echo unsafe")
                    }
                },
                CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains("blocked", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
