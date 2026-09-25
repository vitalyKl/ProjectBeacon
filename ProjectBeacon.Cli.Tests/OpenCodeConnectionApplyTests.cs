namespace ProjectBeacon.Cli.Tests;

using System.Text.Json;
using ProjectBeacon.Cli.Client;

public sealed class OpenCodeConnectionApplyTests
{
    [Fact]
    public void WritesProvider_AndKeepsTheKeyOutOfTheFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "beacon-oc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "opencode.json");
        try
        {
            var applied = WorkstationActions.ApplyOpenCodeConnections(
                """[{"providerId":"xai","modelId":"grok-3","baseUrl":"","apiKey":"secret-key"}]""",
                path);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("secret-key", text);
            Assert.Contains("{env:BEACON_OC_XAI}", text);
            Assert.Equal("secret-key", applied.Environment["BEACON_OC_XAI"]);
            using var doc = JsonDocument.Parse(text);
            Assert.Equal("grok-3", doc.RootElement.GetProperty("provider").GetProperty("xai").GetProperty("models").GetProperty("grok-3").GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
