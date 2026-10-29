namespace ProjectBeacon.Application.Tests;

using Application.Agents;

public sealed class McpCatalogTests
{
    [Fact]
    public void Build_AlwaysIncludesBeacon()
    {
        var mcp = McpCatalog.Build(@"A:\repo", context7: false, serena: false);
        Assert.True(mcp.ContainsKey("beacon"));
        Assert.False(mcp.ContainsKey("context7"));
        Assert.Equal("local", mcp["beacon"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Build_CatalogAndCustom()
    {
        var mcp = McpCatalog.Build("/repo", context7: true, serena: true,
        [
            new CustomMcpServer("docs", "remote", null, "https://example.com/mcp"),
            new CustomMcpServer("beacon", "local", "ignored", null)
        ]);
        Assert.True(mcp.ContainsKey("context7"));
        Assert.True(mcp.ContainsKey("serena"));
        Assert.Equal("https://example.com/mcp", mcp["docs"]!["url"]!.GetValue<string>());
        Assert.Equal("beacon", mcp["beacon"]!["command"]![0]!.GetValue<string>());
    }
    [Fact]
    public void BuildTyped_PreservesLocalArgumentBoundaries()
    {
        var mcp = McpCatalog.BuildTyped("/repo", false, false,
        [
            new LocalMcpServer("custom", "node", ["server.js", "--workspace", "/path/with spaces"]),
            new RemoteMcpServer("remote", new Uri("https://example.com/mcp"))
        ]);

        Assert.Equal("node", mcp["custom"]!["command"]![0]!.GetValue<string>());
        Assert.Equal("/path/with spaces", mcp["custom"]!["command"]![3]!.GetValue<string>());
        Assert.Equal("https://example.com/mcp", mcp["remote"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void BuildTyped_RejectsInvalidRemoteUrl()
    {
        Assert.Throws<ArgumentException>(() => McpCatalog.BuildTyped("/repo", false, false,
            [new RemoteMcpServer("bad", new Uri("file:///tmp/mcp"))]));
    }

}
