namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Common;
using Application.Devices;

public sealed class CommandSandboxTests
{
    private static string Payload(string path) =>
        new JsonObject { ["path"] = path }.ToJsonString();

    [Theory]
    [InlineData(@"C:\secret")]
    [InlineData(@"C:/secret")]
    [InlineData(@"\\server\share\secret")]
    [InlineData(@"//server/share/secret")]
    [InlineData(@"/absolute/path")]
    public void SanitizeProjectPayload_RootedPaths_Rejected(string path)
    {
        var result = CommandSandbox.SanitizeProjectPayload(Payload(path));
        Assert.False(result.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, result.Error);
    }

    [Theory]
    [InlineData(@"../escape")]
    [InlineData(@"..\escape")]
    [InlineData(@"a/../b")]
    [InlineData(@"a\..\b")]
    public void SanitizeProjectPayload_Traversal_Rejected(string path)
    {
        var result = CommandSandbox.SanitizeProjectPayload(Payload(path));
        Assert.False(result.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, result.Error);
    }

    [Theory]
    [InlineData(@"relative/path")]
    [InlineData(@"relative\path")]
    [InlineData(@"file..name.cs")]
    public void SanitizeProjectPayload_RelativePaths_Accepted(string path)
    {
        var result = CommandSandbox.SanitizeProjectPayload(Payload(path));
        Assert.True(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Value!);
        Assert.Equal(path, doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void SanitizeProjectPayload_NullByte_Rejected()
    {
        var result = CommandSandbox.SanitizeProjectPayload(Payload("src\0evil"));
        Assert.False(result.Success);
        Assert.Equal(WorkspacePath.RelativePathRequired, result.Error);
    }
}
