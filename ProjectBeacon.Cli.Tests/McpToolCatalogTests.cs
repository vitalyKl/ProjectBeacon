namespace ProjectBeacon.Cli.Tests;

using ProjectBeacon.Cli.Mcp;
using ProjectBeacon.Domain.Common;
using Xunit;

public sealed class McpToolCatalogTests
{
    [Fact]
    public void BuiltInServerDefinitions_MatchRegistryExactly()
    {
        var serverNames = McpStdioServer.Tools()
            .Select(tool => tool?["name"]?.GetValue<string>() ?? throw new InvalidOperationException("Tool has no name."))
            .ToArray();
        var catalogNames = McpToolCatalog.All.Select(descriptor => descriptor.Name).ToArray();

        Assert.Equal(serverNames.Length, serverNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(catalogNames.OrderBy(name => name, StringComparer.Ordinal),
            serverNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void ApiToolDefinitions_MatchApiHandlerDescriptorsExactly()
    {
        var apiNames = McpApiTools.Definitions()
            .Select(tool => tool["name"]?.GetValue<string>() ?? throw new InvalidOperationException("API tool has no name."))
            .ToArray();
        var registeredNames = McpToolCatalog.All
            .Where(descriptor => descriptor.Handler == McpToolHandler.Api)
            .Select(descriptor => descriptor.Name)
            .ToArray();

        Assert.Equal(registeredNames.OrderBy(name => name, StringComparer.Ordinal),
            apiNames.OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(apiNames, name => Assert.True(McpApiTools.IsApiTool(name)));
    }

    [Fact]
    public void RegistryAliases_ResolveToTheirCanonicalDescriptor()
    {
        foreach (var descriptor in McpToolCatalog.All)
        {
            Assert.True(McpToolCatalog.TryGet(descriptor.Name, out var canonical));
            Assert.Same(descriptor, canonical);
            Assert.Contains("beacon_" + descriptor.Name, descriptor.Aliases);
            foreach (var alias in descriptor.Aliases)
            {
                Assert.True(McpToolCatalog.TryResolve(alias, out var resolved));
                Assert.Same(descriptor, resolved);
            }
        }
    }
}
