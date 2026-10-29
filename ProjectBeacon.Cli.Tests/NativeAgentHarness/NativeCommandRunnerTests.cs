namespace ProjectBeacon.Cli.Tests.NativeAgentHarness;

using System.Text.Json.Nodes;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class NativeCommandRunnerTests
{
    [Fact]
    public async Task DisabledOrUnlistedCommandsAreRejectedWithoutStartingAProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-command-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var args = new JsonObject
            {
                ["executable"] = "dotnet",
                ["arguments"] = new JsonArray { JsonValue.Create("--version") }
            };
            var disabled = await NativeCommandRunner.ExecuteAsync(root, args, [], CancellationToken.None);
            Assert.True(disabled.IsError);
            Assert.Contains("AllowedCommandPrefixes", disabled.Text, StringComparison.Ordinal);

            var mismatch = await NativeCommandRunner.ExecuteAsync(root, args, ["dotnet test"], CancellationToken.None);
            Assert.True(mismatch.IsError);
            Assert.Contains("does not match", mismatch.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AllowlistedCommandRunsWithoutShellAndReturnsBoundedResult()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-command-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var args = new JsonObject
            {
                ["executable"] = "dotnet",
                ["arguments"] = new JsonArray { JsonValue.Create("--version") },
                ["timeoutSeconds"] = 15
            };
            var result = await NativeCommandRunner.ExecuteAsync(root, args, ["dotnet --version"], CancellationToken.None);
            Assert.False(result.IsError);
            Assert.Contains("Exit code: 0", result.Text, StringComparison.Ordinal);
            Assert.Contains("STDOUT:", result.Text, StringComparison.Ordinal);
        }
        finally
        {
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
                ["executable"] = "dotnet",
                ["arguments"] = new JsonArray { JsonValue.Create("test"), JsonValue.Create(argument) }
            };
            var result = await NativeCommandRunner.ExecuteAsync(root, args, ["dotnet test"], CancellationToken.None);
            Assert.True(result.IsError);
            Assert.Contains("absolute paths and parent-directory traversal", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AbsoluteAndParentTraversalArgumentsAreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-command-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var args = new JsonObject
            {
                ["executable"] = "dotnet",
                ["arguments"] = new JsonArray { JsonValue.Create("test"), JsonValue.Create("../outside.sln") }
            };
            var result = await NativeCommandRunner.ExecuteAsync(root, args, ["dotnet test"], CancellationToken.None);
            Assert.True(result.IsError);
            Assert.Contains("parent-directory traversal", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
