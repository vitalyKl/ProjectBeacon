namespace ProjectBeacon.Cli.Tests.NativeAgentHarness;

using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class NativeCommandRunnerTests
{
    [Fact]
    public void ObviousDangerousCommandsAreRejectedBeforeStartingAProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-command-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(CommandExecutionPolicy.TryValidate(["sudo", "dotnet", "test"], root, out var sudoError));
            Assert.Contains("blocked", sudoError, StringComparison.OrdinalIgnoreCase);

            Assert.False(CommandExecutionPolicy.TryValidate(["git", "push", "origin", "main"], root, out var pushError));
            Assert.Contains("git push", pushError, StringComparison.OrdinalIgnoreCase);

            Assert.False(CommandExecutionPolicy.TryValidate(["npm", "install", "-g", "tool"], root, out var globalError));
            Assert.Contains("Global", globalError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ArgvCommandRunsWithoutShellAndReturnsStructuredResultAndLogId()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-command-test-" + Guid.NewGuid().ToString("N"));
        var sessionId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        try
        {
            var args = new JsonObject
            {
                ["argv"] = new JsonArray { JsonValue.Create("dotnet"), JsonValue.Create("--version") },
                ["timeoutSeconds"] = 15
            };
            var result = await NativeCommandRunner.ExecuteAsync(root, sessionId, args, CancellationToken.None);
            Assert.False(result.IsError, result.Text);

            using var document = JsonDocument.Parse(result.Text);
            var payload = document.RootElement;
            Assert.Equal(0, payload.GetProperty("exitCode").GetInt32());
            Assert.True(payload.GetProperty("durationMs").GetInt64() >= 0);
            Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("outputTail").GetString()));
            var runId = payload.GetProperty("runId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(runId));

            var log = await NativeCommandRunner.ReadLogAsync(sessionId,
                new JsonObject { ["runId"] = runId, ["maxLines"] = 20 }, CancellationToken.None);
            Assert.False(log.IsError, log.Text);
            using var logDocument = JsonDocument.Parse(log.Text);
            Assert.Equal(runId, logDocument.RootElement.GetProperty("runId").GetString());
            Assert.True(logDocument.RootElement.GetProperty("lines").GetArrayLength() > 0);
        }
        finally
        {
            NativeCommandRunner.CleanupSession(sessionId);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("--project=../outside.csproj")]
    [InlineData("--logger:file=../../outside.log")]
    [InlineData("--project=/outside/project.csproj")]
    [InlineData("--project=C:\\outside\\project.csproj")]
    public async Task EmbeddedAbsoluteAndParentTraversalArgumentsAreRejected(string argument)
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-command-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var args = new JsonObject
            {
                ["argv"] = new JsonArray { JsonValue.Create("dotnet"), JsonValue.Create("test"), JsonValue.Create(argument) }
            };
            var result = await NativeCommandRunner.ExecuteAsync(root, Guid.NewGuid().ToString("N"), args, CancellationToken.None);
            Assert.True(result.IsError);
            Assert.Contains("Absolute paths and parent-directory traversal", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RunLogRejectsPathsAndUnknownRunIds()
    {
        var session = Guid.NewGuid().ToString("N");
        var missing = await NativeCommandRunner.ReadLogAsync(session,
            new JsonObject { ["runId"] = "../outside" }, CancellationToken.None);
        Assert.True(missing.IsError);
        Assert.Contains("invalid", missing.Text, StringComparison.Ordinal);
        NativeCommandRunner.CleanupSession(session);
    }
}
