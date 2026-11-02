namespace ProjectBeacon.Cli.Tests.NativeAgentHarness;

using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class NativeShellRunnerTests
{
    [Fact]
    public async Task RunsShellCommandInProjectDirectoryAndReturnsBoundedOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-shell-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await NativeShellRunner.ExecuteAsync(
                root,
                new JsonObject
                {
                    ["command"] = "echo beacon-shell-ok",
                    ["timeoutSeconds"] = 15
                },
                CancellationToken.None);

            Assert.False(result.IsError, result.Text);
            Assert.Contains("Exit code: 0", result.Text, StringComparison.Ordinal);
            Assert.Contains("beacon-shell-ok", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RejectsEmptyCommandAndInvalidTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-shell-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var missing = await NativeShellRunner.ExecuteAsync(
                root,
                new JsonObject { ["command"] = " " },
                CancellationToken.None);
            var timeout = await NativeShellRunner.ExecuteAsync(
                root,
                new JsonObject { ["command"] = "echo ok", ["timeoutSeconds"] = 301 },
                CancellationToken.None);

            Assert.True(missing.IsError);
            Assert.Contains("required", missing.Text, StringComparison.Ordinal);
            Assert.True(timeout.IsError);
            Assert.Contains("timeoutSeconds", timeout.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
