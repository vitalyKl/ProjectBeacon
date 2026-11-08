namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Common;

public sealed class BuiltInAgentProfilesTests
{
    [Fact]
    public void CatalogContainsEightBuiltInProfilesWithRequiredLifecycleTools()
    {
        Assert.Equal(8, BuiltInAgentProfiles.All.Count);
        var required = new[]
        {
            McpToolNames.SubtaskReportResult,
            McpToolNames.PipelineFailSubtask,
            McpToolNames.WorkspaceWrite
        };

        foreach (var profile in BuiltInAgentProfiles.All)
        {
            Assert.All(required, tool => Assert.Contains(tool, profile.RequiredTools));
            Assert.All(profile.Tools, tool =>
            {
                Assert.True(McpToolCatalog.TryResolve(tool, out var descriptor), $"Missing MCP tool {tool}.");
                Assert.NotEqual(McpToolScope.Admin, descriptor!.Scope);
            });
            Assert.DoesNotContain(profile.Tools, tool => tool is
                McpToolNames.TaskCreateSubtask
                or McpToolNames.PipelineStartActor
                or McpToolNames.PipelineLaunchSession
                or McpToolNames.PipelineStartReview);
        }
    }

    [Fact]
    public void ReadOnlyAndCommandPoliciesMatchTheBuiltInProfileIntent()
    {
        var explorer = AssertProfile("explorer");
        Assert.Equal(AgentProfileMode.Plan, explorer.Mode);
        Assert.False(explorer.CommandsEnabled);
        Assert.DoesNotContain(McpToolNames.WriteFile, explorer.Tools);
        Assert.DoesNotContain(McpToolNames.ApplyPatch, explorer.Tools);

        var verifier = AssertProfile("verifier");
        Assert.Equal(AgentProfileMode.Act, verifier.Mode);
        Assert.True(verifier.CommandsEnabled);
        Assert.DoesNotContain(McpToolNames.WriteFile, verifier.Tools);
        Assert.DoesNotContain(McpToolNames.ApplyPatch, verifier.Tools);

        var docs = AssertProfile("docs-writer");
        Assert.False(docs.CommandsEnabled);
        Assert.Equal(AgentProfilePathPolicy.Defaults, docs.PathPolicy);
        Assert.True(BuiltInAgentProfiles.IsWithinDefaults("docs/architecture.md", docs.DefaultPaths));
        Assert.True(BuiltInAgentProfiles.IsWithinDefaults("README.md", docs.DefaultPaths));
        Assert.False(BuiltInAgentProfiles.IsWithinDefaults("../secrets.txt", docs.DefaultPaths));
        Assert.False(BuiltInAgentProfiles.IsWithinDefaults("/etc/passwd", docs.DefaultPaths));
    }

    [Fact]
    public void UnknownProfileDoesNotSilentlyResolveToADefault()
    {
        Assert.Null(BuiltInAgentProfiles.Get("unknown-profile"));
    }

    private static BuiltInAgentProfile AssertProfile(string key)
    {
        var profile = BuiltInAgentProfiles.Get(key);
        Assert.NotNull(profile);
        return profile!;
    }
}
