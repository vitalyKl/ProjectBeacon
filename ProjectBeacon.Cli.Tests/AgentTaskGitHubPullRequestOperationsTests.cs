namespace ProjectBeacon.Cli.Tests;

using System.Text.Json.Nodes;
using Domain.Common;
using ProjectBeacon.Cli.Client.AgentHarness;

public sealed class AgentTaskGitHubPullRequestOperationsTests
{
    [Fact]
    public void GitPullRequest_IsARegisteredStructuredGitTool()
    {
        Assert.True(AgentTaskGitOperations.IsGitTool(PipelineToolPolicy.GitPullRequest));
    }

    [Theory]
    [InlineData("Actor", true)]
    [InlineData("Planner", false)]
    [InlineData("Review", false)]
    [InlineData("Eval", false)]
    [InlineData(null, false)]
    public void OnlyTheActorMayMarkItsTaskPullRequestReady(string? role, bool expected)
    {
        Assert.Equal(expected, AgentTaskGitHubPullRequestOperations.CanMarkReady(role));
    }

    [Fact]
    public void GitPullRequestDefinition_OnlyExposesTaskBoundStatusAndReadyActions()
    {
        var definition = AgentTaskGitOperations.ToolDefinition(PipelineToolPolicy.GitPullRequest);
        var parameters = definition["function"]?["parameters"] as JsonObject;
        var properties = parameters?["properties"] as JsonObject;
        var action = properties?["action"]?["enum"] as JsonArray;

        Assert.NotNull(properties);
        Assert.NotNull(action);
        Assert.Equal(2, action.Count);
        Assert.Equal(new[] { "status", "ready" },
            action.Select(value => value?.GetValue<string>()).ToArray());
        Assert.False(properties.ContainsKey("pullRequestNumber"));
        Assert.False(properties.ContainsKey("repository"));
        Assert.False(properties.ContainsKey("remote"));
        Assert.False(properties.ContainsKey("branch"));
        Assert.False(properties.ContainsKey("base"));
        Assert.False(properties.ContainsKey("merge"));
    }
}
